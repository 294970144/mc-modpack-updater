#!/usr/bin/env python
"""
xaml_resource_lint.py —— XAML 资源引用静态检查

【为什么需要这个工具】
M0 探针实测踩过的坑：Palette.xaml 里定义了 Color `McBgGradA` 但忘了定义对应的
Brush `BrushBgGradA`，MainWindow.xaml 引用了它。dotnet build **全绿**，
但运行时构造函数抛 XamlParseException，窗口连出都出不来（双击即退）。

StaticResource 的错误引用是编译期不报的。这是 WinUI 3 开发里最容易浪费
构建轮次的一类错误 —— 一轮 build + publish + 启动 = 4~6 分钟才能发现。

所以把它变成静态检查：build 之前跑，缺资源立刻报出来。

【检查项】
  1. StaticResource / ThemeResource 引用的 key 是否都有定义
     （含跨文件：MainWindow.xaml 可以引用 Palette.xaml 的 key）
  2. 资源字典内部自引（Palette.xaml 里的 Brush 引用 Color）是否悬空
  3. XML 注释里的连续双减号（`--`）—— XML 规范非法，XAML 编译器报 WMC9997
     这是从 CSS 变量原名直接抄注释时最容易踩的
  4. 同一文件内重复定义同一个 x:Key（后者静默覆盖前者，很难发现）
  5. 未被使用的资源（仅提示，不报错 —— 有意保留的备选资源是正常的）

【用法】
  python xaml_resource_lint.py <项目目录或文件...>
  退出码 0 = 通过，1 = 有 ERROR

【维护】
  新增 XAML 文件后无需改动本工具 —— 它扫描给定目录下所有 *.xaml。
"""

from __future__ import annotations

import io
import os
import re
import sys
from collections import defaultdict

# WinUI / 框架自带资源，不在项目资源字典里
FRAMEWORK_KEYS = {
    "XamlControlsResources",
    # 通用 WinUI 资源字典里常用的系统键（本项目不定义，靠 XamlControlsResources 带入）
    "ApplicationPageBackgroundThemeBrush",
    "SystemControlBackgroundAccentBrush",
    "LayerFillColorDefaultBrush",
    "CardStrokeColorDefaultBrush",
    "CardBackgroundFillColorDefaultBrush",
    "ControlStrokeColorDefaultBrush",
    "AccentFillColorDefaultBrush",
    "AccentStrokeColorDefaultBrush",
    "TextFillColorPrimary",
    "TextFillColorSecondary",
    "TextFillColorTertiary",
    "AccentTextFillColorPrimaryBrush",
    "ChevronGlyph",
}

# 资源 key 命名规范：本项目约定 L1 Color = Mc*，L2 Brush = Brush*，
# 度量 = 半PascalCase。出现别的形态大概率是拼错。
KEY_SHAPE = re.compile(r"^(Mc[A-Z]|Brush[A-Z]|Font[A-Z]|FontSize|Radius|ThinEdge|CardEdge|SoftLine|TopBarEdge|HardShadow|x:Double)")
KEY_VALUE_SHAPE = re.compile(r"^(Mc[0-9A-Za-z]*|Card[0-9]?|Win[T][0-9]?)$")

RE_KEY_DEF = re.compile(r'x:Key="([^"]+)"')
RE_STATIC_REF = re.compile(r'\bStaticResource\s+([A-Za-z_][A-Za-z0-9_]*)')
RE_THEME_REF = re.compile(r'\bThemeResource\s+([A-Za-z_][A-Za-z0-9_]*)')
RE_COLOR_DEF = re.compile(
    r'<Color\s+x:Key="([^"]+)"\s*>(#[0-9A-Fa-f]{8})</Color>'
)
# ResourceDictionary 里作为 x:Key 定义的元素类型。
# Style 必须包含：BasedOn="{StaticResource XxxStyle}" 是最常见的自引用形式，
# 漏了它会把所有 BasedOn 误报成「未定义 key」（M1 实测踩到）。
RE_DEF_ELEMENT = re.compile(
    r'<(Color|SolidColorBrush|LinearGradientBrush|Thickness|x:Double|'
    r'CornerRadius|FontFamily|FontSize|x:String|x:Boolean|x:Int32|'
    r'Style|ControlTemplate|DataTemplate|'
    r'XamlControlsResources)\b[^>]*x:Key="([^"]+)"'
)

# 【M1 实测踩坑 —— 本工具最重要的一条规则】
# 跨「合并进同一个 Application.Resources 的多个 ResourceDictionary 文件」
# 的 StaticResource 引用会运行期解析失败。
# 实测症状：Palette.xaml 定义 FontUi，Controls.xaml 的
#   <Setter Property="FontFamily" Value="{StaticResource FontUi}" />
# 编译全绿，启动即抛
#   XamlParseException: The property 'FontUi' was not found in type 'Button'.
# 原因：Setter 在解析/应用时目标字典的资源尚不可见，退化成
# 「在 Button 上找 FontUi 属性」→ 找不到 → 异常。
#
# 注意这与「页面 XAML 引用 Application.Resources」无关：那是单向引用，一直正常。
# 所以本规则只针对「Setter 内部引用了别的字典文件里定义的 key」。
RE_SETTER = re.compile(r'<Setter\b[^>]*\bValue="([^"]*)"\s*/?>')

# 【M1 实测踩坑 —— 第二个真正致命的坑】
# TemplateBinding 只能指向**控件的真实属性**。误把资源 key 写进去
# （FontFamily="{TemplateBinding FontUi}" —— FontUi 是 x:Key，不是属性）
# 编译全绿，启动即抛：
#   XamlParseException: The property 'FontUi' was not found in type 'Button'.
# 这个报错信息极具误导性：它看起来像「资源没找到」，实际是「属性不存在」，
# 于是很容易被误诊成资源字典的作用域问题（我为此白排查了两轮，
# 先怀疑跨字典 StaticResource，又怀疑 FontFamily 资源类型不被支持）。
#
# 判定：TemplateBinding 引用的名字，若**不是** x:Key 定义的资源，
# 就必须能在目标类型上找到对应属性。资源 key 与属性名同名的情形极少，
# 所以「不是资源 key」就足以判定为可疑 —— 报 ERROR 让作者确认。
RE_TEMPLATE_BINDING = re.compile(r'\{TemplateBinding\s+([A-Za-z_][A-Za-z0-9_.]*)\}')

TAG_COMMENT = "<!--"
END_COMMENT = "-->"


def strip_comments_keep_map(text: str) -> str:
    """把 XML 注释替换成等长空格，保持所有偏移不变。

    必须在剥离注释之后再做正则匹配，否则注释里出现的
    `StaticResource Xxx` 会被误报成真实引用。
    """
    out = list(text)
    i = 0
    n = len(text)
    while i < n:
        s = text.find(TAG_COMMENT, i)
        if s < 0:
            break
        e = text.find(END_COMMENT, s + len(TAG_COMMENT))
        if e < 0:
            # 未闭合的注释：WMC9997，会直接让构建失败
            for k in range(s, n):
                out[k] = " "
            break
        for k in range(s, e + len(END_COMMENT)):
            out[k] = " "
        i = e + len(END_COMMENT)
    return "".join(out)


def line_of(text: str, pos: int) -> int:
    return text.count("\n", 0, pos) + 1


class Finding:
    __slots__ = ("level", "path", "line", "msg")

    def __init__(self, level: str, path: str, line: int, msg: str):
        self.level = level
        self.path = path
        self.line = line
        self.msg = msg

    def __str__(self) -> str:
        rel = os.path.basename(self.path)
        return f"[{self.level}] {rel}:{self.line}  {self.msg}"


def collect_files(targets: list[str]) -> list[str]:
    out: list[str] = []
    for t in targets:
        if os.path.isfile(t) and t.lower().endswith(".xaml"):
            out.append(os.path.abspath(t))
        elif os.path.isdir(t):
            for root, dirs, files in os.walk(t):
                # 跳过构建产物与包缓存
                dirs[:] = [
                    d for d in dirs
                    if d not in {"obj", "bin", ".vs", "packages", "node_modules"}
                ]
                for f in files:
                    if f.lower().endswith(".xaml"):
                        out.append(os.path.abspath(os.path.join(root, f)))
    return sorted(set(out))


def lint(files: list[str]) -> list[Finding]:
    findings: list[Finding] = []

    # key -> [(path, line)]，全局定义表
    defined: dict[str, list[tuple[str, int]]] = defaultdict(list)
    raw: dict[str, str] = {}
    refs: list[tuple[str, str, int, str]] = []  # (kind, path, line, key)
    setter_refs: list[tuple[str, int, str]] = []  # (path, line, key)
    tbrefs: list[tuple[str, int, str]] = []  # (path, line, 属性名)

    for path in files:
        try:
            text = io.open(path, encoding="utf-8-sig").read()
        except OSError as ex:
            findings.append(Finding("ERROR", path, 0, f"读取失败: {ex}"))
            continue
        raw[path] = text

        # --- 检查 3：注释里的双减号（必须在剥注释之前查） ---
        pos = 0
        while True:
            s = text.find(TAG_COMMENT, pos)
            if s < 0:
                break
            e = text.find(END_COMMENT, s + 4)
            if e < 0:
                findings.append(Finding(
                    "ERROR", path, line_of(text, s),
                    "XML 注释未闭合（报 WMC9997，构建直接失败）",
                ))
                break
            body_start = s + 4
            body = text[body_start:e]
            # 注释正文里不允许出现 --
            for m in re.finditer(r"--", body):
                snippet = body[max(0, m.start() - 12): m.start() + 12].replace("\n", " ")
                findings.append(Finding(
                    "ERROR", path, line_of(text, body_start + m.start()),
                    f"XML 注释正文含连续双减号: ...{snippet}... "
                    f"（抄 CSS 变量原名如 muted 前缀时会中招）",
                ))
            pos = e + 3

        code = strip_comments_keep_map(text)

        # --- 定义收集 ---
        for m in RE_DEF_ELEMENT.finditer(code):
            defined[m.group(2)].append((path, line_of(text, m.start())))

        # --- 引用收集 ---
        for m in RE_STATIC_REF.finditer(code):
            refs.append(("StaticResource", path, line_of(text, m.start()), m.group(1)))
        for m in RE_THEME_REF.finditer(code):
            refs.append(("ThemeResource", path, line_of(text, m.start()), m.group(1)))

        # --- 检查 6：Setter 跨字典引用（判定延后，需完整的 defined 表） ---
        for m in RE_SETTER.finditer(code):
            for km in RE_STATIC_REF.finditer(m.group(1)):
                setter_refs.append((path, line_of(text, m.start()), km.group(1)))

        # --- 检查 7：TemplateBinding 收集（判定同样延后） ---
        for m in RE_TEMPLATE_BINDING.finditer(code):
            tbrefs.append((path, line_of(text, m.start()), m.group(1)))

    # --- 检查 1 & 2：悬空引用 ---
    for kind, path, ln, key in refs:
        if key in defined or key in FRAMEWORK_KEYS:
            continue
        # ThemeResource 允许运行时解析，宽松处理为 WARN
        level = "ERROR" if kind == "StaticResource" else "WARN"
        findings.append(Finding(
            level, path, ln,
            f"{kind} 引用了未定义的 key '{key}' "
            f"→ 编译期不报，运行时 XamlParseException（窗口出不来）",
        ))

    # --- 检查 6：Setter 跨字典引用 ---
    # 【本规则的 premise 曾是错的，2026-10-09 已订正】
    # 原先断言「跨字典 StaticResource 会抛 XamlParseException」，
    # 并建议「合并成单字典」。那是误诊 —— 干净重建后错误依旧。
    # M1 的 FontUi崩溃真因是 {TemplateBinding 资源key}（检查 7 抓到了），
    # 与跨字典无关：XAML 的资源解析本就是逐层向上查找 Application.Resources，
    # 跨文件合并字典互相引用是官方支持的用法。
    #
    # 本规则降级为 INFO 保留：跨文件合并确实值得人看一眼顺序/可见性，
    # 但它**不是**错误，不该以 ERROR 阻断，也不该给出「必须合并」的错误处方。
    for path, ln, key in setter_refs:
        places = defined.get(key, [])
        # 定义在同一文件内 → 无需提示
        if any(p == path for p, _ in places):
            continue
        if key in FRAMEWORK_KEYS:
            continue
        where = ", ".join(f"{os.path.basename(p)}:{l}" for p, l in places) or "（未找到定义）"
        findings.append(Finding(
            "INFO", path, ln,
            f"Setter 引用了另一个字典文件里定义的资源 '{key}'（定义在 {where}）。"
            f"这是合法用法，**不会**导致启动崩溃；仅提示你确认该字典已正确合并进"
            f" App.xaml 的 MergedDictionaries 且顺序在前。",
        ))


    # --- 检查 7：TemplateBinding 指向资源 key（而不是控件属性） ---
    # 只报「确实是资源 key」这一种 —— 那是真正的坑：它长得像合法的资源引用。
    # 刻意**不**对「未知的 PascalCase 名」报警：那会命中 Background/Foreground/Padding
    # 等几十个正常属性，噪音一大规则就会被习惯性忽略，反而更糟。
    for path, ln, name in tbrefs:
        if name in defined:
            findings.append(Finding(
                "ERROR", path, ln,
                f"TemplateBinding 指向 '{name}'，但它是**资源 key**（x:Key），"
                f"不是控件属性 → 编译全绿，启动即抛 "
                f"XamlParseException: The property '{name}' was not found in type 'Button'。"
                f"该报错极具误导性（像「资源没找到」，实为「属性不存在」）。"
                f"要用资源请写 StaticResource；要用属性请写真实属性名。",
            ))
    # --- 检查 4：重复定义 ---
    for key, places in defined.items():
        if len(places) > 1:
            where = ", ".join(f"{os.path.basename(p)}:{l}" for p, l in places)
            findings.append(Finding(
                "ERROR", places[0][0], places[0][1],
                f"x:Key='{key}' 重复定义 {len(places)} 处（后者静默覆盖前者）: {where}",
            ))

    # --- 检查 5：未被使用（提示级） ---
    used = {k for _, _, _, k in refs}
    unused = sorted(k for k in defined if k not in used and k != "XamlControlsResources")
    if unused:
        findings.append(Finding(
            "INFO", files[0] if files else "", 0,
            f"定义了但当前无人引用的资源 {len(unused)} 个: {', '.join(unused)}",
        ))

    return findings


def main(argv: list[str]) -> int:
    targets = argv[1:] or ["."]
    files = collect_files(targets)
    if not files:
        print("未找到任何 .xaml 文件")
        return 1

    findings = lint(files)
    errors = [f for f in findings if f.level == "ERROR"]
    warns = [f for f in findings if f.level == "WARN"]
    infos = [f for f in findings if f.level == "INFO"]

    for f in errors + warns + infos:
        print(f)

    print()
    print(f"扫描 {len(files)} 个 XAML 文件："
          f"{len(errors)} ERROR, {len(warns)} WARN, {len(infos)} INFO")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
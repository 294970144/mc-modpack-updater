# PROJECT_STATE — 我的世界更新助手（WinUI 3 重制）

> 本文件是**跨对话交接载体**，记录当前成果、已确认决定与明确的第一步。
> 流水账式的过程记录在 `winui/M0-探针结论.md` 与 `.workbuddy/memory/YYYY-MM-DD.md`，本文件不重复。
>
> 更新时间：2026-10-10

---

## 交接区

| 项 | 值 |
| --- | --- |
| 本次交接编号 | `HO-20261010-01` |
| 源任务编号 | 未确认（宿主未提供） |
| 用户确认的目录／动作范围 | 保存交接；2026-10-14 追加授权**提交**（**仍未授权推送**） |
| 接手任务编号 | （留空，未创建，在同一目录原任务上继续） |
| 交接状态 | **材料已核对**（仅保存，未新建接续任务） |
| 恢复后进展 | 2026-10-10 完成 **M4**；2026-10-14 完成 **M5** + 视觉验收 + **已提交** `a87be7d`（未推送） |

---

## 1. 任务与版本

**目标**：把 Node 版「我的世界更新助手」按 WinUI 3 规范重写为 Windows 原生应用，
UI 复刻 Node 版的温馨像素风（不用 Fluent 原生视觉）。

**工作目录**：`C:\Users\zhang\WorkBuddy\Worktrees\我的世界toos\main-cec55022`

**分支／提交状态**：

- 当前分支 `workbuddy/main-cec55022`
- **`HEAD` = `a87be7d feat(winui): M0-M5 WinUI 3 重制（选包 → 选目标 → 对比 → 变更清单）`
  —— 89 个文件 / +20523 行，M0 至今的成果已全部入 git（2026-10-14 提交）**
- 父提交 `3e95b55 release: v1.1.0 — 文件选择体验重做（SendUp 风格）`（Node 版的最后一个提交）
- **未推送**（未授权）。要推送时：`git push -u origin workbuddy/main-cec55022`

**提交前补了 `.gitignore`**：`bin/` `obj/` `*.user` `.vs/` `*.suo` `*.userprefs`
`publish/` `McUpdater/logs/`。winui/ 的 bin+obj 合计 **846 MB**（bin 742M / obj 96M），
不加忽略会卷进约 1900 个中间产物 —— 真实源码只有 65 个文件。

**⚠️ 主工作树 `E:/mc/mc26/mcnf/我的世界toos`（main 分支）另有别人的未提交改动，我没碰**：
14 个已跟踪文件被改动（+482/−143：`src/core/applier.js` +208、`src/router.js` +145、
`src/core/zip-reader.js` +52、`public/js/app.js` +74、`src/server.js`、`src/config.js`…），
外加未跟踪的 `CHANGELOG.md`、`fuji-wiki-kb/`（159 文件）、`winui/.parity`（空壳）。
三个工作树（main / main-4f2b947c / main-cec55022）原来都停在 3e95b55。

**本次范围（已由用户确认）**：选包 → 选目标 → 对比 → 变更清单 UI，到 M5 为止。

**明确不做**：apply、rollback、backup、设置、导出、Inno 安装包、JS↔C# 全量自动对拍。

**完成标准**：按里程碑验收。**M0–M5 全部完成**（apply / rollback / backup / 设置 / 导出不在范围内）。

---

## 2. 权威资料

| 资料 | 路径（相对项目根） | 管哪一部分 |
| --- | --- | --- |
| Node 原版源码 | `src/`、`public/` | **保真的唯一权威**。行为疑问先查这里，不要凭直觉 |
| Node 版设计源 | `public/css/main.css`、`public/index.html` | 像素风配色的唯一来源（996 行 CSS） |
| WinUI 踩坑记录 | `winui/M0-探针结论.md` | M0 探针结论 + 已踩坑清单，**改WinUI 代码前必读** |
| 过程日志 | `.workbuddy/memory/2026-10-09.md` | 每日流水账，含真实包基准数据与保真要点 |
| Node 基准生成器 | `tools/gen-*.js`、`tools/sync-real-baseline.js` | 期望值来源。**禁止手抄基准** |

**测试样本（只读，禁改原文件）**：

- `D:/Users/zhang/Downloads/1.21.11 1.0.6.zip`（226MB / mrpack 本体 / overrides 内容根 / Fabric 0.19.3 / MC 1.21.11）
- `E:/mc/pcl2`（PCL2 版本库在 `<根>/.minecraft/versions/`）

---

## 3. 决定与限制

### 用户已确认

- 严格按请求范围执行。「信息密度高」≠ 可自行扩大交付范围；要扩展结构或功能**先问再做**。
- 真实样本仅供只读测试。
- Picker 双弹 bug 已确认修复（容器 `Tapped` 已移除，只保留按钮入口）。
- 里程碑逐个验收，不跳步。

### Agent 自行选择（可复议）

- **xUnit 测试台用 `Compile Include` 反向链接主程序的 `Core/`**，而非复制源码。
  理由：避免「测试通过但产品是错的」这种最危险的假绿。TFM刻意用 `net10.0`（不引 WinUI），
  这样 Core 里一旦混入 WinUI 依赖会**编译失败**——用工具链强制 Core 纯净性，不靠 code review。
- **放弃截图验收，改用程序内自检** `--selftest`。原因见第 5 节。
- **不移植 Node 的 512MB 上限**：那是 HTTP `POST` 上传的 body 限制，本机直读路径没有上传环节。
  但保留 `AppConfig.InMemoryThreshold`（128MB 落缓存）这条真实防线。

### 已否决的路线（附原因，别重试）

| 路线 | 原因 |
| --- | --- |
| `PrintWindow` / `BitBlt` / `CopyFromScreen` 抓 WinUI 窗口 | DirectComposition 合成，客户区全白；无头会话只能拍到桌面。**但 `ffmpeg -f gdigrab -i desktop` 整屏抓是通的**（2026-10-10 实证）—— 别再试窗口句柄那三条，直接抓整屏再裁 |
| `Application.RequestedTheme`锁浅色 | unpackaged 下抛 `COMException 0x80131515`，双击即退。正确做法：`RequestedTheme="Light"` 写在视觉树根 Grid |
| `EnableMsixTooling=true` | .NET 10 下编译失败（MSB4018）。csproj 里直接写 `false` |
| 给 `ScanLocalMods` 加 `OrderBy` 让顺序确定 | **会改变用户可见的展示顺序**。Node 用 `readdirSync` 原生顺序，不是字母序。被 106 条真实基准当场抓住 |
| `UniformGrid` 四列布局 | WPF 才有，WinUI 不存在（XamlCompiler WMC0001） |
| 手抄测试期望值 | 本轮手写错了 5 处，被 Node 实跑逐一推翻。必须用 `tools/sync-real-baseline.js` 自动誊 |
| `Process.WaitForExit(timeout)` 之后再 `ReadToEnd()` | 匿名管道只有几 KB，子进程写满即阻塞 → 永远不退出 → 白等满超时（实测 6039ms，伪装成"扫盘很慢"）。必须**先** `ReadToEndAsync()` 并发排空 |
| 合成目录测试里断言"文件夹总共 N 个" | 预设根目录 `PCL2_DEFAULT_ROOTS` 会额外贡献文件夹 → 装了 PCL2 的机器红、没装的绿。要按 `Pcl2Root` 过滤 |

### 未决

- 步骤 2 面板的像素级视觉**无截图证据**，需用户亲眼看。
  已知风险点：`内含` 一行最多 4 个标签（最宽「配置文件 191」），在两栏布局的半宽卡片里可能换行或溢出。

### 环境限制（本机，跨对话有效）

- `dotnet build/test` 前必须
  `export APPDATA='C:\Users\zhang\AppData\Roaming' ProgramFiles='C:\Program Files' SystemRoot='C:\Windows'`
  —— 沙箱缺这三个变量会导致 NuGet restore 报 `path1` null。加 `--disable-build-servers` 更稳。
- 中文路径在 Bash 里 `cd` 有时失败，用引号包裹可解决。
- Git Bash 下 stdout 的中文是乱码（codepage 问题），**日志文件本身是 UTF-8 正常**。
- `rm -rf` 被 safe-delete 钩子拦截，清理 `obj`/`bin` 用 `mv`。
- 截图后必须 `taskkill //IM McUpdater.exe //F`，否则残留实例锁住 DLL。

---

## 4. 进展与运行状态

### 已完成

| 里程碑 | 内容 |
| --- | --- |
| **M0** | 技术探针，五个结论全部定论（见 `M0-探针结论.md`）。全案无未验证的技术假设 |
| **M1** | 骨架 + 像素风资源字典 + 背景动画跑通 |
| **M2** | Core 纯逻辑移植：ZipReader / ModpackReader / ChangeItem / Manifest 只读侧 / DiffEngine |
| **M3** | 读包链路 + 步骤 2 面板（整合包信息卡 + 更新范围勾选） |
| **M3 补修** | 进度卡 35% 的死锁（详见第 5 节） |
| **M4** | PCL2 / 多实例目录扫描 + 版本匹配 + 目标卡（详见第 5.1 节） |
| **M5** | 变更清单 UI：接 DiffEngine、筛选/搜索/勾选/统计/冲突提示（详见第 5.2 节） |

**代码结构**：

```
winui/McUpdater/
├── Core/          纯逻辑，不引WinUI（12 个文件）
├── Services/      读包链路与展示映射（5 个文件）
├── Controls/      StepBar
├── Backgrounds/   PixelBackground
├── Styles/        Theme.xaml（配色+度量+字体+控件样式合一）
└── MainWindow.xaml(.cs) / App.xaml(.cs) / SelfTest.cs
winui/tests/McUpdater.Tests/   17 个测试文件（含 1 个自动生成）
```

### 验证证据

| 项 | 结果 | 怎么复现 |
| --- | --- | --- |
| 回归测试 | **297 全绿**（M3 的 256 + M4 新增 41） | `cd winui/tests/McUpdater.Tests && dotnet test --disable-build-servers` |
| 构建 | 0 警告 0 错误 | `cd winui/McUpdater && dotnet build -c Debug --disable-build-servers` |
| 真实包自检 | 问题 0 项，退出码 0 | `McUpdater.exe --selftest "D:/Users/zhang/Downloads/1.21.11 1.0.6.zip"` |
| 读包性能 | 约 37ms | 同上，自检输出耗时 |
| 目标扫描性能 | 约 667ms（进程探测 646 + 扫盘 21） | 同上，「分段耗时」一行 |

自检会同时验证数据映射、**真实控件构造**（每项 `ApplyTemplate()`）
与**目标卡真渲染**（把所有 `x:Name` 走一遍）。

测试台警告已清零（2026-10-14：顺手修了 M2 遗留的 `ManifestTests.cs:51` CS8625
—— `MakeTarget` 参数该是 `string?`；`ModpackReaderTests.cs:347` xUnit2013 —— 改 `Assert.Single`）。

### 未做

- 范围内已无未做项。范围外（apply / rollback / backup / 设置 / 导出）按约定不做

### 未验证

- ~~步骤 2 面板像素级视觉~~ → **已有整屏截图** `winui/M4-步骤2实拍.png`：
  顶栏/步骤条/三张卡/范围勾选/保护说明全部渲染正确，
  **M3 担心的「内含 4 个标签挤爆半宽卡片」没有发生** —— 一行放得下。
  截图右侧被桌面上的远控悬浮窗压住（那是个置顶窗），目标卡的「目录」行
  与第 3、4 个范围勾选被遮，需一张无遮挡的确认
- 「手动选择」后的卡片换版（需要真的点一遍）

### 截图怎么拍（已验证的路子）

```bash
# 1) 启动并渲染好步骤 2（--show：自检后显示窗口，固定 1280x840 @(80,80)）
McUpdater.exe --selftest "D:/Users/zhang/Downloads/1.21.11 1.0.6.zip" --show
# 2) 整屏抓取（gdigrab 抓的是桌面，不是窗口句柄 —— 绕开 DirectComposition）
ffmpeg -f gdigrab -i desktop -frames:v 1 -update 1 shot.png
# 3) 按已知窗口位置裁剪
ffmpeg -i shot.png -vf "crop=1280:840:80:80" -frames:v 1 -update 1 out.png
# 4) 用完关进程，否则残留实例锁 DLL
taskkill //IM McUpdater.exe //F
```

注意：截的是 Master 的真实桌面。**他正在用机器时别反复弹窗截屏**，
先问一句或等空闲；悬浮窗（远控/IM）多为置顶，会压住画面。

### 运行中的后台任务

无。

---

## 5. 本轮完成：进度卡 35% 的死锁修复

**现象**：选包后进度条停在 35%、窗口点不动。

**根因**：读包一直是好的——`ModpackReader.Open` 的刻度就是 `5→15→25→30→35`，
**最后一条本就是 35**（不是 100，100% 由 UI 层补）。真正的故障在**读包之后**：
异常发生在 `await` 返回后的 `RenderScope()`，而 `HideOverlay()` 写在它后面 →
遮罩留在屏幕上封死窗口 →看起来就是「读包卡在 35%」。
异常还被`PickAndLoadAsync` 的 catch 吃掉，只往HintText 写一句，
而 HintText 正被遮罩盖着 → **真实错误信息完全不可见**。

**三处真 bug**：

1. `MainWindow.xaml.cs` 的 `((Grid)box.Children[0])` 强转 —— 加进去的是 `Border` → `InvalidCastException`
2. `Theme.xaml` 的 `Setter Target="CheckMark.Visibility"` —— 勾号在 Content 里，
   而 VisualState 的 `Setter Target` 只在 ControlTemplate 命名空间内解析
3. `HandlePackChosenAsync` 的遮罩没被 `finally` 兜住（**结构性根因**）

**沉淀的方法论（重要）**：

> **数据映射全对≠ 界面能用。**
> 这次 `PackInfoMapper` 输出完全正确、256 个测试全绿，但一行控件强转就让功能不可用。
> 数据层测试对这类 bug 天然无感—— 它压根不碰控件。
> 所以自检里加了「真实构造控件 + ApplyTemplate」这一段。

> **凡是「开遮罩 → 干活 → 关遮罩」，关遮罩必须放`finally`。**
> 否则任何一个后置异常都会把它变成死锁。

**顺带撞出的 WinUI 3 API 差异**（写之前查，别猜）：

- `ToggleButton` **没有 `IsCheckedChanged`**（那是 `CheckBox` 的），只有 `Checked`/`Unchecked`
- `Border` 只能有一个子元素；把勾号塞进去会被内容对齐覆盖而贴边 → 改用兄弟节点

---

## 5.1 本轮完成：M4 目标选择

### 交付内容

| 文件 | 作用 |
| --- | --- |
| `Core/ClientVersion.cs` | `GameFolder` / `Pcl2RootInfo` / `ClientVersion` / `Pcl2DetectResult` |
| `Core/Pcl2Detector.cs` | 移植 `src/services/pcl2-detector.js` 全文（含进程探测） |
| `Core/ClientScanner.cs` | 移植 `src/services/client-scanner.js` 全文 |
| `Core/AppConfig.cs` | 补 `PCL2_*` 与 `MC_*` 常量 |
| `Services/TargetMapper.cs` | 目标卡展示模型 + `BuildWarnings`（来自 `router.js` 的 `/api/target`） |
| `MainWindow.xaml(.cs)` | 目标卡真实内容（版本/现有模组/目录 + 标签 + 警告 + 匹配依据）、「手动选择」 |
| `SelfTest.cs` | M4 段：分段计时、扫描结果 dump、标签构造、**目标卡真渲染** |
| `tools/gen-pcl2-baseline.js` + `sync-pcl2-baseline.js` | 基准生成器与自动誊写（产出 `RealTargetBaseline.cs`） |

### ★ 真 bug：进程探测白等 6 秒（匿名管道死锁）

自检首次输出「扫描用时 6071 ms」，一眼看去像"扫盘很慢"。
分段计时立刻定位到**进程探测 6039 ms，扫盘只有 30 ms**。

根因是 .NET 重定向输出的经典死锁：管道缓冲区只有几 KB，
而 `tasklist /FO CSV /NH` 的输出十几 KB。写成
「先 `WaitForExit(6000)` 再 `ReadToEnd()`」时，子进程写满管道阻塞 →
永远退不出去 → 我们白等满整个超时，一个字节的盘都没扫。

**修法**：`Start` 之后立刻 `ReadToEndAsync()` 并发排空 stdout/stderr，再 `WaitForExit`。
6039ms → 646ms。

> **方法论**：这类 bug 的伪装性极强 —— 它不讲"我超时了"，只讲"我很慢"。
> 凡是"跑外部命令 + 重定向输出"，读必须在等待之前开始，不能之后。

### 真实环境基准（`E:/mc/pcl2` × `1.21.11 1.0.6.zip`）

| 项 | 值 |
| --- | --- |
| 启动器目录 | 1（`E:/mc/pcl2`） |
| 游戏文件夹 | 3：`当前文件夹`(pcl2/.minecraft)、`yks`、`Client1.21.11`（**选中**） |
| 游戏版本 | 3：`Fabulously Optimized`(1.20.1/fabric/50 mod)、`1.21.11`×2（yks 91 mod、Client 90 mod） |
| 自动匹配 | `1.21.11 @ Client1.21.11`，**195 分** = 100+30+15+25+20+5 |

yks 那个 `1.21.11` 得 183 分（文件夹未被选中 → 选中项只 +8 而非 +20），
`Fabulously Optimized` 得 28 分（只有加载器 + 有 mod）。

### 本轮新增的保真要点（已全部落测试）

- **加载器优先级两侧不同**：本地版本侧是 fabric→**neoforge**→forge→quilt，
  整合包侧（ModpackReader）是 fabric→quilt→forge→**neoforge**。Node 两个文件就这么写的，别"统一"
- **排序必须稳定**：`matchTarget` 用 `OrderByDescending`（LINQ 稳定），
  不能用 `List.Sort`（不稳定）。同分要保扫描顺序
- **空串等于没有**：`if (setup.VersionFabric)` 在 JS 里对 `VersionFabric:` 不成立，
  直译 `TryGetValue` 会把空串当成有效加载器名
- **日志去重跨文件**：`seen` 在整个 `parseFoldersFromLogs` 生命周期内有效，不按文件重置
- `resolveIsolation` 的四个分支（files / mixed / config / default）逐分支照搬 ——
  猜错内容目录会把文件写到错误位置

### 测试为什么按 `Pcl2Root` 过滤而不是断言总数

合成目录的测试若断言"总共 3 个文件夹"，在这台装了 PCL2 的机器上会因为
预设根目录（`config.js` 的 `PCL2_DEFAULT_ROOTS`）额外贡献文件夹而变红 ——
**在没装 PCL2 的机器上却是绿的**。这种测试等于埋雷。
现在只断言"属于被测根目录的那几条"，机器无关。

同理，`Detect` 全部显式注入 `probe`（固定 `running=false`）：
「PCL2 此刻有没有在跑」是本机瞬时状态，基准也是未运行时取的。

### 主动砍掉的部分（附理由，等 Master 定夺）

**「手动选择」只做了文件夹选择器，没做 Node 的候选列表弹窗。**
理由：候选列表需要一整套模态列表控件；自动匹配已覆盖绝大多数场景
（版本名/游戏版本/加载器三重对齐），手动指定是兜底。
若 M5 要支持"切换目标重新对比"，那时补列表更顺。

### 未验证项（如实记录）

目标卡**没有截图证据**（WinUI 客户区抓不到，见第 5 节）。
逻辑层已由 `--selftest` 与 297 个测试证明，
但「标签换行好不好看」「手动指定后的卡片换版」需 Master 亲眼看。

---
## 5.2 本轮完成：M5 变更清单 UI

### 交付内容

| 文件 | 作用 |
| --- | --- |
| `Core/DiffPlanner.cs` | 移植 `src/router.js` 的 `runDiff`：分类编排、`TYPE_ORDER` 排序、`summary`、`mergeConflicts`、skipped 累加 |
| `Core/ChangeList.cs` | 移植 `app.js` 的 `rowHtml` / `visibleChanges` / `renderChanges` / `renderStatus` |
| `MainWindow.xaml(.cs)` | 步骤 3 面板：提示框、筛选芯片、搜索、分组列表、勾选、底部统计 |
| `Styles/Theme.xaml` | 三档 notice、变更清单外框、分组头、**无外观切换按钮**（见下） |
| `SelfTest.cs` | M5 段：真跑一次对比 + dump 前 15 行 + 渲染步骤 3 + 5 条不变量 |
| `tools/gen-diffplan-baseline.js` + `sync-diffplan-baseline.js` | 完整链路的 Node 基准（产出 `RealDiffPlanBaseline.cs`） |

### ★ 自检抓到的真 bug：`FontFamily = null`

渲染步骤 3 时炸出 `'Unknown' is not a valid value for property 'FontFamily'`。
原因：`TextBlock.FontFamily` 不能赋 `null` —— 那会被当成字符串 "Unknown"，
而且**只在控件真正进视觉树时才炸**，构造阶段风平浪静。
"用默认字体"只能不设这个属性。这条已写进代码注释。

### 基准抓到的保真 bug：跳过样本少了分类前缀

Node 在 `runDiff` **聚合**阶段给样本加前缀（`${cat.dir}/${s}`）。
我只搬了 `DiffFiles` 返回的分类内相对路径，少了 `config/` 这层 ——
用户看到的"例如：.puzzle_cache/..."就不知道是哪个分类下的。已修。

### 真实环境基准（264 项，8.8 MB）

| 项 | 值 |
| --- | --- |
| 变更总数 | 264（新增 1 / 更新 14 / 移除 14 / 无变化 235） |
| 勾选中写入量 | 8.8 MB |
| 玩家自装移除项 | 13 |
| 跳过 | 保护规则 86 个 |
| 重复冲突 | 0 |

### 本轮新增的保真要点

- **分类顺序取配置顺序**，不是用户勾选顺序（用户 scope 是 HashSet，顺序不定）
- **`summary.bytes` 只统计勾选中的非无变化项** —— 统计全部会让"写入约 XX"虚高到没有决策价值
- **「全部」视图藏掉无变化项**，但「无变化」筛选项又能单独看到
- **状态栏统计的是全部变更，不是当前可见的那部分** —— 否则搜索一个词会让"已勾选 N 项"跟着变
- **`duplicate` 与 `duplicated` 互斥**（else-if），本地多余项要说"保留哪一份"
- **`FirstRun` 决定移除项默认全不勾**（防止误删玩家自装的 mod）

### 遗留任务清理（2026-10-14，Master 指示）

- ✅ 测试台 2 个 M2 遗留警告 → 已修，测试台警告清零
- ⚠️ 基准漂移：**Master 4 天里给 1.21.11 装了 5 个 mod**（90 → 95），
  M4/M5 真实基准全部过期。已重新生成，并把测试改成**环境漂移免疫**：
  - M4 按版本身份（contentDir）配对，modCount 只做合理性检查；
  - M5 记录基准时的本地 jar 数（94），不一致就**跳过**而不是误报
    ——「我的代码错了」和「Master 装了 mod」必须能分开
- ✅ **步骤 3 已有截图**：`winui/M5-步骤3实拍.png`（2026-10-14 会话解锁后补拍）。
  顶栏/步骤条/warn 提示框/五枚筛选芯片/分组头/变更行（勾选、徽章、标签、
  版本对比、体积）/底部统计全部渲染正确。
- ⚠️ **「手动指定」态截图未拿到**：截的时候 Master 的聊天窗口正压在应用上面
  （他还开着机器）。数据层已由自检验证（`[ok] 手动指定态标签：手动指定`），
  截图随时可补：`--show --target "E:/mc/pcl2/yks/.minecraft/versions/1.21.11"`。
  【顺带修掉一个 bug】`--target` 最初只改控件值没切回步骤 2 面板，
  截出来还是步骤 3 —— `SetTargetForSelfTest` 已补 `ShowPanel(2)`。
- ⚠️ **会话锁定时截图整条路不通**：`gdigrab` 抓到的是锁屏而不是会话桌面。
  截图前先抓一张确认没在锁屏。

### 截图命令（等会话解锁后跑）

```bash
McUpdater.exe --selftest "<包路径>" --show --step3          # 步骤 3
McUpdater.exe --selftest "<包路径>" --show --target "E:/mc/pcl2/yks/.minecraft/versions/1.21.11"   # 手动指定态
```

`--show` 固定窗口 1280x840 @(80,80) 并置顶；截完 `taskkill //IM McUpdater.exe //F`。

---

## 6. 接续动作

### 下一步：全部里程碑已交付，等 Master 验收

**范围内没有未完成的里程碑。** 接下来两件事，都需 Master 拍板：

1. **人肉验收**：跑一次真实流程（选包 → 看目标卡 → 对比 → 翻变更清单）。
   步骤 3 的视觉只有逻辑层证据（自检 + 320 测试），截图被锁屏挡住了。
2. ~~**提交 git**~~ → 已完成：`a87be7d`（89 文件 / +20523 行），M0–M5 全部入 git。
   **仍未推送** —— 要推时一条命令：`git push -u origin workbuddy/main-cec55022`。
3. ⚠️ **主工作树的 14 个未提交改动归谁处理**：那些是别人的活（applier / router 方向，
   看起来是"应用更新"那块，本次明确不做）。要不要提交、提交到哪，等 Master 决定。

**验收方法**：

```bash
export APPDATA='C:\Users\zhang\AppData\Roaming' ProgramFiles='C:\Program Files' SystemRoot='C:\Windows'
cd winui/tests/McUpdater.Tests && dotnet test --disable-build-servers   # 320 全绿，0 警告
cd ../McUpdater && dotnet build -c Debug --disable-build-servers        # 0 警告 0 错误
./bin/Debug/net10.0-windows10.0.26100.0/win-x64/McUpdater.exe \
    --selftest "D:/Users/zhang/Downloads/1.21.11 1.0.6.zip"                # 问题 0 项
```

**基准过期了怎么办**（真实样本会随玩家改动游戏目录而漂移）：

```bash
node tools/gen-pcl2-baseline.js     > tools/pcl2-baseline.json     && node tools/sync-pcl2-baseline.js
node tools/gen-diffplan-baseline.js > tools/diffplan-baseline.json && node tools/sync-diffplan-baseline.js
```

**必须遵守的约束**：

1. 期望值必须由 Node 原版生成 —— 四套 gen-*/sync-* 工具链都在 `tools/`
2. 保持目录枚举的原生顺序
3. Core 保持 `net10.0` 纯净
4. 保真规则写在代码注释里，注明 Node 出处
5. 「开遮罩 → 干活 → 关遮罩」，关遮罩必须在 `finally`
6. **真实样本只读** —— 连 CRC 缓存都不落盘（`DiffPlanner` 刻意用内存态 MetaCache）

---

## 恢复开场白

新对话开场可直接用：

```
$project-handoff

项目路径：C:\Users\zhang\WorkBuddy\Worktrees\我的世界toos\main-cec55022
交接来源：PROJECT_STATE.md（交接编号 HO-20261010-01）

当前目标：WinUI 3 重制「我的世界更新助手」，**M0–M5 全部完成**，等验收与决定是否提交 git。

已确认限制：
- 范围到 M5 变更清单为止；不做 apply / rollback / backup / 设置 / 导出
- Node 版 src/ 与 public/ 是保真的唯一权威，先查它再动手
- 真实样本 D:/Users/zhang/Downloads/1.21.11 1.0.6.zip 与 E:/mc/pcl2 只读
- dotnet 命令前须export APPDATA/ProgramFiles/SystemRoot

第一步：读 PROJECT_STATE.md 第 6 节，验收 + 决定是否把 winui/ 提交进 git。
验收：320 测试全绿 + 构建 0 警告 + --selftest 问题 0 项。
```
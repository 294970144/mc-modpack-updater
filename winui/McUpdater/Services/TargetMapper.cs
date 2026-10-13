using McUpdater.Core;

namespace McUpdater.Services;

/// <summary>
/// 目标卡上的一个标签（Node <c>renderTargetInfo</c> 里的 <c>span.tag</c>）。
/// </summary>
internal sealed record TargetTagView(string Text, string StyleKey);

/// <summary>
/// 步骤 2「更新到哪个版本」卡的展示模型。
///
/// <para><b>与 <see cref="PackInfoMapper"/> 同样的理由</b>：把「版本对象 → 卡片文案」
/// 抽成纯函数，规则才能被断言。放在这里而不是 MainWindow 里，
/// 是因为它跟整合包卡那一份是同一层东西（读包/扫描结果 → 展示）。</para>
/// </summary>
internal sealed record TargetCardView
{
    /// <summary>有没有匹配到目标。false 时卡片显示「没能自动认出」提示。</summary>
    public required bool HasTarget { get; init; }

    /// <summary>版本名。</summary>
    public string Name { get; init; } = "";

    /// <summary>所属游戏文件夹的显示名（跟在版本名后面，Node 的 muted small 括号）。</summary>
    public string FolderName { get; init; } = "";

    /// <summary>状态标签（PCL2 当前选中 / 本工具管理中 / 手动指定 / 未开版本隔离）。</summary>
    public IReadOnlyList<TargetTagView> Tags { get; init; } = [];

    /// <summary>现有模组数量。</summary>
    public int ModCount { get; init; }

    /// <summary>目录行（缩短后的内容目录路径）。</summary>
    public string DirLine { get; init; } = "";

    /// <summary>风险提示（整合包与目标的 MC 版本/加载器对不上、目标一个 mod 都没有）。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// 「自动匹配依据：…」那一行；手动指定时为空串。
    /// Node：<c>t.why && t.why.length && !t.manual</c> 才渲染。
    /// </summary>
    public string WhyLine { get; init; } = "";
}

/// <summary>
/// 目标卡内容映射（对照 Node <c>public/js/app.js</c> 的 <c>renderTargetInfo</c>
/// 与 <c>src/router.js</c> 里 <c>POST /api/target</c> 的 warnings 生成）。
/// </summary>
internal static class TargetMapper
{
    /// <summary>标签样式与 Node 的 class 对应关系（tag-green / tag-blue / tag-purple / tag-amber）。</summary>
    private const string StyleGreen = "PixelTagGreenStyle";
    private const string StyleBlue = "PixelTagBlueStyle";
    private const string StylePurple = "PixelTagPurpleStyle";
    private const string StyleAmber = "PixelTagAmberStyle";

    /// <summary>
    /// 生成风险提示（Node <c>POST /api/target</c> 的 warnings）。
    ///
    /// <para><b>三条判据一条都不能少</b>：MC 版本不一致、加载器不一致、
    /// 目标一个 mod 都没有。第三条看着像废话，实际抓的是「选错了空文件夹」——
    /// 玩家把整合包更新到一个全新版本上，多半是目录选错了。</para>
    /// </summary>
    public static List<string> BuildWarnings(ModpackHandle? pack, ClientVersion? target)
    {
        var warnings = new List<string>();
        if (pack is null || target is null)
        {
            return warnings;
        }

        if (!string.IsNullOrEmpty(pack.GameVersion) && !string.IsNullOrEmpty(target.GameVersion)
            && !string.Equals(pack.GameVersion, target.GameVersion, StringComparison.Ordinal))
        {
            warnings.Add(
                $"整合包是 {pack.GameVersion}，这个版本是 {target.GameVersion}，加载器和 mod 可能不兼容");
        }

        var packLoader = (pack.Loader ?? "").ToLowerInvariant();
        if (packLoader.Length > 0 && !string.IsNullOrEmpty(target.Loader)
            && !string.Equals(packLoader, target.Loader, StringComparison.Ordinal))
        {
            warnings.Add($"整合包用的是 {pack.Loader}，这个版本是 {target.Loader}，装上去大概率起不来");
        }

        if (target.ModCount == 0)
        {
            warnings.Add("这个版本目前一个 mod 都没有，确认是要更新的那个吗？");
        }

        return warnings;
    }

    /// <summary>
    /// 目标卡展示模型（Node <c>renderTargetInfo</c>）。
    /// </summary>
    /// <param name="target">选中的目标版本；null 表示没匹配上。</param>
    /// <param name="warnings"><see cref="BuildWarnings"/> 的结果。</param>
    /// <param name="shortPath">路径缩短函数（Node <c>UI.shortPath</c>）。</param>
    public static TargetCardView Build(ClientVersion? target, IReadOnlyList<string> warnings,
        Func<string, int, string> shortPath)
    {
        if (target is null)
        {
            return new TargetCardView { HasTarget = false };
        }

        // 【保真】标签顺序固定：选中 → 管理中 → 手动 → 未隔离。
        // Node 是按这个顺序 push 的 <span>，顺序换了对用户就是一种"排版变了"。
        var tags = new List<TargetTagView>();
        if (target.IsSelected) tags.Add(new TargetTagView("PCL2 当前选中", StyleGreen));
        if (target.Managed) tags.Add(new TargetTagView("本工具管理中", StyleBlue));
        if (target.Manual) tags.Add(new TargetTagView("手动指定", StylePurple));
        if (!target.Isolated) tags.Add(new TargetTagView("未开版本隔离", StyleAmber));

        return new TargetCardView
        {
            HasTarget = true,
            Name = target.Name,
            FolderName = target.FolderName ?? "",
            Tags = tags,
            ModCount = target.ModCount,
            DirLine = shortPath(target.ContentDir, 46),
            Warnings = warnings,
            WhyLine = target.Why.Count > 0 && !target.Manual
                ? "自动匹配依据：" + string.Join("、", target.Why)
                : "",
        };
    }
}

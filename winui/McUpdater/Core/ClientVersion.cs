namespace McUpdater.Core;

/// <summary>
/// 一个「游戏文件夹」（Node <c>pcl2-detector.detect()</c> 返回的 folder）。
///
/// <para>PCL2 允许多个游戏文件夹共存（版本库各自独立），
/// 所以「版本」必须带上它属于哪个文件夹 —— 否则两个同名版本无法区分。</para>
/// </summary>
/// <param name="Name">显示名（日志里记的名字 / 目录名）。</param>
/// <param name="Path">.minecraft 目录绝对路径。</param>
/// <param name="Source">这条线索从哪来：log / setup / root / scan。</param>
/// <param name="Pcl2Root">它属于哪个 PCL2 根目录。</param>
/// <param name="IsSelected">是不是 PCL2 当前选中的文件夹。</param>
public sealed record GameFolder(string Name, string Path, string Source, string Pcl2Root, bool IsSelected);

/// <summary>
/// 一个 PCL2 启动器根目录（Node 的 <c>roots[]</c> 项）。
/// </summary>
/// <param name="Path">启动器根目录。</param>
/// <param name="Source">custom / process / preset / scan。</param>
/// <param name="IsActive">正在运行的 PCL2 是不是这个目录里的那个。</param>
/// <param name="Setup">该根目录的 <c>PCL/Setup.ini</c> 解析结果。</param>
public sealed record Pcl2RootInfo(
    string Path,
    string Source,
    bool IsActive,
    IReadOnlyDictionary<string, string> Setup);

/// <summary>
/// 一个可更新的游戏版本（Node <c>client-scanner.scanVersions()</c> 的返回项）。
///
/// <para><b>字段为什么摊平而不是分成「扫描结果」和「匹配结果」两个类型</b>：
/// Node 侧就是同一个对象一路 <c>{...v, ...}</c> 展开下去的 ——
/// <c>scanVersions</c> 产出基础字段，<c>scanAll</c> 叠上文件夹信息，
/// <c>matchTarget</c> 再叠上 <c>score/why</c>。拆成多个类型会让
/// 「什么时候有哪些字段」变成隐式契约，对拍时最容易错位。</para>
/// </summary>
public sealed record ClientVersion
{
    /// <summary>版本名（<c>versions/&lt;name&gt;</c> 的目录名）。</summary>
    public required string Name { get; init; }

    /// <summary>版本目录（<c>versions/&lt;name&gt;</c>）。</summary>
    public required string VersionDir { get; init; }

    /// <summary>
    /// 内容目录 —— mod / config 实际所在的地方。
    /// 开了版本隔离时等于 <see cref="VersionDir"/>，否则是 .minecraft 根。
    /// </summary>
    public required string ContentDir { get; init; }

    /// <summary>是否开了版本隔离。</summary>
    public required bool Isolated { get; init; }

    /// <summary>
    /// 隔离结论的依据（files / mixed / config / default / manual），
    /// 排查"为什么选错目录"时这是唯一线索。
    /// </summary>
    public required string IsolationBasis { get; init; }

    /// <summary>原版 MC 版本号（Setup.ini 的 <c>VersionVanillaName</c>），可能为 null。</summary>
    public string? GameVersion { get; init; }

    /// <summary>加载器（fabric / forge / neoforge / quilt / vanilla），可能为 null。</summary>
    public string? Loader { get; init; }

    /// <summary>加载器版本。</summary>
    public string? LoaderVersion { get; init; }

    /// <summary>Setup.ini 的 <c>Info</c>，PCL2 自己显示的那行描述。</summary>
    public string? Info { get; init; }

    /// <summary>是不是这个游戏文件夹里当前选中的版本（读自 PCL.ini）。</summary>
    public bool IsSelected { get; init; }

    /// <summary>mod 数量。</summary>
    public int ModCount { get; init; }

    /// <summary>各分类计数：mods 是 jar 个数，其余只记「有没有」（0 或 1）。</summary>
    public IReadOnlyDictionary<string, int> Counts { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>之前用本工具更新过（内容目录里有安装记录文件）。</summary>
    public bool Managed { get; init; }

    /// <summary>所属游戏文件夹的显示名（<c>scanAll</c> 叠加）。</summary>
    public string? FolderName { get; init; }

    /// <summary>所属游戏文件夹路径（<c>scanAll</c> 叠加）。</summary>
    public string? FolderPath { get; init; }

    /// <summary>所属游戏文件夹是不是 PCL2 当前选中的（<c>scanAll</c> 叠加）。</summary>
    public bool FolderSelected { get; init; }

    /// <summary>是不是用户手动指定的目录（<c>describeDir</c> 产出）。</summary>
    public bool Manual { get; init; }

    /// <summary>匹配得分（<c>matchTarget</c> 叠加）。</summary>
    public int Score { get; init; }

    /// <summary>得分构成说明，会显示成「自动匹配依据：…」。</summary>
    public IReadOnlyList<string> Why { get; init; } = [];
}

/// <summary>
/// <c>Pcl2Detector.Detect</c> 的结果（Node <c>detect()</c> 的返回值）。
/// </summary>
public sealed record Pcl2DetectResult(
    bool Found,
    bool Running,
    string? ExePath,
    IReadOnlyList<Pcl2RootInfo> Roots,
    IReadOnlyList<GameFolder> Folders);

/// <summary>PCL2 进程探测结果（Node <c>checkRunning()</c>）。</summary>
public sealed record Pcl2ProcessState(bool Running, string? ExePath);

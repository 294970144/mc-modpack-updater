namespace McUpdater.Core;

/// <summary>
/// 配置项（移植自 Node 版 <c>src/config.js</c>）。
///
/// <para>Node 版配置有几十项，这里<em>只搬实际引用到的</em>。
/// 不是全量搬家 —— 那些属于 applier / HTTP 服务端的项（<c>PORT</c>、
/// <c>BACKUP_*</c> 等）本次范围用不到，带过来只是噪音。
/// M4 起补进了 <c>PCL2_*</c> 与 <c>MC_*</c>（目标选择链路要用）。</para>
/// </summary>
public static class AppConfig
{
    /// <summary>
    /// config 分类的单文件大小上限：8 MB。
    ///
    /// <para>Node 版语义：只有 <c>category === 'config'</c> 时才生效，
    /// 其他分类是 <c>Infinity</c>（无上限）。C# 里用 <see cref="long"/> +
    /// <see cref="Unlimited"/> 表达无上限 —— <em>不用 <c>int.MaxValue</c></em>：
    /// 虽然 8MB 阈值下不会溢出，但语义要准确，将来若加别的分类用大文件就踩雷。</para>
    /// </summary>
    public const long ConfigMaxFile = 8L * 1024 * 1024;

    /// <summary>"无上限"的哨兵。用 long.MaxValue 而非 int.MaxValue，见上。</summary>
    public const long Unlimited = long.MaxValue;

    /// <summary>安装记录文件名（Node: <c>MANIFEST_NAME</c>）。</summary>
    public const string ManifestName = ".mcupdater-manifest.json";

    /// <summary>mrpack 的 modrinth 索引文件名（Node: <c>MRPACK_INDEX</c>）。</summary>
    public const string MrpackIndex = "modrinth.index.json";

    /// <summary>mrpack 的 overrides 目录名（Node: <c>MRPACK_OVERRIDES</c>）。</summary>
    public const string MrpackOverrides = "overrides";

    /// <summary>内层包进内存的阈值：128 MB（Node: <c>INMEMORY_THRESHOLD</c>）。</summary>
    public const long InMemoryThreshold = 128L * 1024 * 1024;

    /// <summary>
    /// 分类内部的保护规则（Node: <c>CATEGORY_PROTECTED</c>）。
    /// mod 运行时生成的玩家数据混在 config 里，这些不参与对比、也不显示。
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> CategoryProtected { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["config"] = new[]
            {
                "fuji/backup/**",
                "fuji/cache/**",
                ".puzzle_cache/**",
                "jei/world/**",
                "crash_assistant/**",
                "voicechat/**",
                "e4mc/**",
                "cape-provider/**",
                "**/*.zip",
                "**/*.log",
                "**/*.log.gz",
                "**/*.db",
                "**/*.sqlite",
                "**/*.bak",
                "**/*.tmp",
            },
            ["resourcepacks"] = new[] { "**/*.tmp" },
            ["shaderpacks"] = new[] { "**/*.tmp" },
            // mods 分类没有条目 —— Node 版靠 makeMatcher([]) 返回恒 false。
            // 这里不写这个键，用 GetProtectedPatterns 查不到时返回空数组，
            // 效果与 Node 版一致，且省掉一个空字典项。
        };

    /// <summary>
    /// 取某分类的保护规则，缺省返回空数组。
    ///
    /// <para><b>这里必须兜底而不是直接索引</b>：Node 版写的是
    /// <c>(config.CATEGORY_PROTECTED || {})[categoryKey] || []</c>，
    /// 靠两次 <c>||</c> 兜住"字典里没这个键"。C# 的字典索引器越界会抛
    /// <c>KeyNotFoundException</c>，直译过来会让 <c>mods</c> 分类（确实没有
    /// 保护规则）直接崩掉整个 diff。</para>
    /// </summary>
    public static string[] GetProtectedPatterns(string categoryKey)
        => CategoryProtected.TryGetValue(categoryKey, out var patterns) ? patterns : [];

    // ------------------------------------------------------------------
    // M4：PCL2 探测与版本扫描（Node: src/config.js 的同名项）
    // ------------------------------------------------------------------

    /// <summary>
    /// PCL2 常见安装位置（Node: <c>PCL2_DEFAULT_ROOTS</c>）。
    ///
    /// <para>第一个是 Master 本机的实际位置，后两个是常见兜底。
    /// 顺序有意义：它们决定 <c>detect</c> 里根目录候选的先后顺序，
    /// 进而决定游戏文件夹的枚举顺序（保真要求，别排序）。</para>
    /// </summary>
    public static readonly string[] Pcl2DefaultRoots =
    [
        @"E:\mc\pcl2",
        @"D:\PCL2",
        @"C:\PCL2",
    ];

    /// <summary>PCL2 主程序文件名（Node: <c>PCL2_EXE_NAMES</c>）。</summary>
    public static readonly string[] Pcl2ExeNames =
    [
        "Plain Craft Launcher 2.exe",
        "Plain Craft Launcher.exe",
        "PCL2.exe",
    ];

    /// <summary>启动器配置相对路径（Node: <c>PCL2_SETUP_INI</c> = <c>PCL/Setup.ini</c>）。</summary>
    public const string Pcl2SetupIni = "PCL/Setup.ini";

    /// <summary>日志目录名（Node: <c>PCL2_LOG_DIR</c>）。</summary>
    public const string Pcl2LogDir = "PCL";

    /// <summary>
    /// 日志文件（Node: <c>PCL2_LOG_FILES</c>）。
    /// <b>顺序即优先级</b>：日志是滚动的，Log1 最新，越往后越旧。
    /// </summary>
    public static readonly string[] Pcl2LogFiles =
    [
        "Log1.txt", "Log2.txt", "Log3.txt", "Log4.txt", "Log5.txt",
    ];

    /// <summary>
    /// 日志里「有效的 Minecraft 文件夹」那行的抓取模式（Node: <c>PCL2_LOG_FOLDER_PATTERN</c>）。
    /// 组 1 = 文件夹显示名，组 2 = 路径。
    /// </summary>
    public const string Pcl2LogFolderPattern = @"有效的 Minecraft 文件夹：(.+?) > (.+)";

    /// <summary>版本目录名（Node: <c>MC_VERSIONS_DIR</c>）。</summary>
    public const string McVersionsDir = "versions";

    /// <summary>文件夹级选中版本记录（Node: <c>MC_PCL_INI</c>）。</summary>
    public const string McPclIni = "PCL.ini";

    /// <summary>
    /// 受管分类清单（Node: <c>MANAGED_CATEGORIES</c>）。
    /// 与 Node 版保持顺序与内容一致 —— UI 的分类展示顺序依赖它。
    ///
    /// <para><b>Title 必须逐字对齐 Node 的 <c>label</c></b>：它会原样出现在
    /// 更新范围勾选项与「内含」标签上，属于用户可见文案。M2 移植时写成
    /// 「配置」「光影」，实际是「配置文件」「光影包」，已在 M3 修正。</para>
    /// </summary>
    public static readonly (string Key, string Title, bool DefaultOn, bool ByModId)[] ManagedCategories =
    [
        ("mods", "模组", true, true),
        ("config", "配置文件", false, false),
        ("resourcepacks", "资源包", false, false),
        ("shaderpacks", "光影包", false, false),
    ];

    /// <summary>取某分类的单文件大小上限（config 有上限，其余无上限）。</summary>
    public static long GetMaxFileSize(string categoryKey)
        => string.Equals(categoryKey, "config", StringComparison.OrdinalIgnoreCase)
            ? ConfigMaxFile
            : Unlimited;
}

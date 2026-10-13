using System.Globalization;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace McUpdater.Core;

/// <summary>
/// 游戏版本扫描与目标匹配（移植自 Node 版 <c>src/services/client-scanner.js</c>）。
///
/// <para><b>PCL2 的目录是两层</b>：
/// <c>.minecraft/</c> 是「游戏文件夹」，<c>versions/&lt;name&gt;/</c> 是「版本」。
/// 开了版本隔离时 mods 在版本目录里，没开则在 .minecraft 根（所有版本共用）。</para>
///
/// <para><b>隔离开没开，配置项说了不算</b>：PCL2 那个
/// <c>VersionArgumentIndie</c> 的取值语义各版本之间变过，
/// 所以 Node 以「实际哪个目录里有东西」为准，配置只当参考。
/// 这条判断逻辑必须逐分支照搬 —— 猜错内容目录会直接把文件写到错误位置。</para>
/// </summary>
public static class ClientScanner
{
    // ------------------------------------------------------------------
    // 小工具
    // ------------------------------------------------------------------

    private static readonly Regex JarRegex = new(@"\.jar(\.disabled)?$", RegexOptions.IgnoreCase);

    private static bool IsJar(string name) => JarRegex.IsMatch(name);

    /// <summary>
    /// 目录里的文件数量（不递归，Node <c>countFiles</c>）。目录读不了算 0。
    /// </summary>
    private static int CountFiles(string dir, Func<string, bool>? filter)
    {
        try
        {
            var n = 0;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (filter is null || filter(Path.GetFileName(f)))
                {
                    n++;
                }
            }
            return n;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 判断隔离状态：先看实际文件，再看配置（Node <c>resolveIsolation</c>）。
    /// </summary>
    /// <returns><c>isolated</c> 是否隔离，<c>basis</c> 依据（files/mixed/config/default）。</returns>
    public static (bool Isolated, string Basis) ResolveIsolation(
        string mcRoot, string versionDir, IReadOnlyDictionary<string, string> setup)
    {
        var isoCount = CountFiles(Path.Combine(versionDir, "mods"), IsJar);
        var sharedCount = CountFiles(Path.Combine(mcRoot, "mods"), IsJar);

        if (isoCount > 0 && sharedCount == 0)
        {
            return (true, "files");
        }
        if (sharedCount > 0 && isoCount == 0)
        {
            return (false, "files");
        }
        if (isoCount > 0 && sharedCount > 0)
        {
            // 两边都有 mod：看配置，配置也不明确就按隔离算
            // （整合包场景占绝大多数 —— 非隔离时 mods 会被所有版本共用，很少见）
            var flag = (Value(setup, "VersionArgumentIndieV2") ?? Value(setup, "VersionArgumentIndie") ?? "")
                .ToLowerInvariant();
            var off = flag is "false" or "2" or "0";
            return (!off, "mixed");
        }

        // 两边都空：新装的版本，看配置
        var v2 = (Value(setup, "VersionArgumentIndieV2") ?? "").ToLowerInvariant();
        if (v2 == "true")
        {
            return (true, "config");
        }
        if (v2 == "false")
        {
            return (false, "config");
        }
        var v1 = (Value(setup, "VersionArgumentIndie") ?? "").Trim();
        if (v1 == "1")
        {
            return (true, "config");
        }
        if (v1 is "2" or "0")
        {
            return (false, "config");
        }
        return (true, "default");
    }

    /// <summary>
    /// 取 Setup.ini 的值。<b>空串等于没有</b> —— JS 里空串是 falsy，
    /// <c>if (setup.VersionFabric)</c> 对 <c>VersionFabric:</c> 这行不成立。
    /// 直译成 <c>TryGetValue</c> 会把空串当成有效加载器名。
    /// </summary>
    private static string? Value(IReadOnlyDictionary<string, string> setup, string key)
        => setup.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    // ------------------------------------------------------------------
    // 加载器
    // ------------------------------------------------------------------

    /// <summary>
    /// 从 <c>version.json</c> 里读加载器（Setup.ini 缺失时的兜底，
    /// Node <c>loaderFromVersionJson</c>）。
    ///
    /// <para><b>那个 4000 字符截断要照搬</b>：Node 写的是
    /// <c>JSON.stringify(j.libraries || []).slice(0, 4000)</c>，
    /// 只取前 4000 个字符去匹配。不截断的话，超长依赖列表里某个
    /// 无关条目含 "forge" 字样就会误判 —— 截断本身就是正确性的一部分。</para>
    ///
    /// <para><b>序列化用 <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/></b>：
    /// 默认编码器会把非 ASCII 与 <c>&amp; &lt; &gt; +</c> 转成 <c>\uXXXX</c>，
    /// 与 JS 的 <c>JSON.stringify</c> 不同，会改变截断位置。</para>
    /// </summary>
    public static string? LoaderFromVersionJson(string jsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;

            string? Id(string name)
                => root.TryGetProperty(name, out var v) ? v.GetString() : null;

            var libs = root.TryGetProperty("libraries", out var l)
                ? JsonSerializer.Serialize(l, new JsonSerializerOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                })
                : "[]";

            var blob = string.Join(' ', Id("id") ?? "", Id("inheritsFrom") ?? "",
                libs.Length > 4000 ? libs[..4000] : libs);

            if (Regex.IsMatch(blob, "fabric[-_]?loader", RegexOptions.IgnoreCase)) return "fabric";
            if (Regex.IsMatch(blob, "quilt[-_]?loader", RegexOptions.IgnoreCase)) return "quilt";
            if (Regex.IsMatch(blob, "neoforge", RegexOptions.IgnoreCase)) return "neoforge";
            if (Regex.IsMatch(blob, "minecraftforge|net\\.minecraftforge", RegexOptions.IgnoreCase)) return "forge";
            return "vanilla";
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // 扫描
    // ------------------------------------------------------------------

    /// <summary>
    /// 扫描一个 .minecraft 文件夹下的所有版本（Node <c>scanVersions</c>）。
    ///
    /// <para><b>目录枚举保持原生顺序，不要"顺手"排序</b>：
    /// <c>ScanLocalMods</c> 已经因为擅自加 <c>OrderBy</c>
    /// 被 106 条真实基准当场抓住过一次 —— 排序会改变用户可见的展示顺序。
    /// 这里的顺序由后面那次显式排序决定，中间过程原样保留。</para>
    /// </summary>
    public static List<ClientVersion> ScanVersions(string mcRoot)
    {
        var out_ = new List<ClientVersion>();
        var versionsDir = Path.Combine(mcRoot, AppConfig.McVersionsDir);
        if (!Directory.Exists(versionsDir))
        {
            return out_;
        }

        // 文件夹级 PCL.ini 记录了当前选中的版本
        string? selectedName = null;
        try
        {
            var text = File.ReadAllText(Path.Combine(mcRoot, AppConfig.McPclIni));
            var m = Regex.Match(text, @"^\s*Version\s*:\s*(.+)$", RegexOptions.Multiline);
            if (m.Success)
            {
                selectedName = m.Groups[1].Value.Trim();
            }
        }
        catch
        {
            // 没有就没有
        }

        foreach (var versionDir in Directory.EnumerateDirectories(versionsDir))
        {
            var name = Path.GetFileName(versionDir);

            // 有同名 json 才算一个合法版本
            if (!File.Exists(Path.Combine(versionDir, name + ".json")))
            {
                continue;
            }

            var setup = Pcl2Detector.ParseSetupIniFile(Path.Combine(versionDir, "PCL", "Setup.ini"));
            var iso = ResolveIsolation(mcRoot, versionDir, setup);
            var contentDir = iso.Isolated ? versionDir : mcRoot;

            // 加载器：优先级 fabric → neoforge → forge → quilt
            //
            // 【与 ModpackReader 的顺序不同，不是写错了】
            // 那边（整合包侧）是 fabric → quilt → forge → neoforge，
            // 这边（本地版本侧）是 fabric → neoforge → forge → quilt。
            // Node 两个文件就是这么写的。同一个版本名在两侧被判成不同加载器
            // 会导致匹配失败，所以必须各按各的原版顺序。
            string? loader;
            string? loaderVersion;
            if (Value(setup, "VersionFabric") is { } fab) { loader = "fabric"; loaderVersion = fab; }
            else if (Value(setup, "VersionNeoForge") is { } neo) { loader = "neoforge"; loaderVersion = neo; }
            else if (Value(setup, "VersionForge") is { } forg) { loader = "forge"; loaderVersion = forg; }
            else if (Value(setup, "VersionQuilt") is { } quil) { loader = "quilt"; loaderVersion = quil; }
            else { loader = LoaderFromVersionJson(Path.Combine(versionDir, name + ".json")); loaderVersion = null; }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (key, _, _, _) in AppConfig.ManagedCategories)
            {
                var d = Path.Combine(contentDir, key);
                // 非 mods 只记「有没有」，递归统计留给对比阶段
                counts[key] = key == "mods" ? CountFiles(d, IsJar) : (Directory.Exists(d) ? 1 : 0);
            }

            out_.Add(new ClientVersion
            {
                Name = name,
                VersionDir = versionDir,
                ContentDir = contentDir,
                Isolated = iso.Isolated,
                IsolationBasis = iso.Basis,
                GameVersion = Value(setup, "VersionVanillaName"),
                Loader = loader,
                LoaderVersion = loaderVersion,
                Info = Value(setup, "Info"),
                IsSelected = selectedName == name,
                ModCount = counts["mods"],
                Counts = counts,
                Managed = File.Exists(Path.Combine(contentDir, AppConfig.ManifestName)),
            });
        }

        // 【保真】四级排序，且必须是稳定排序：
        // JS 的 Array.prototype.sort 自 ES2019 起保证稳定，
        // 而 List.Sort 不稳定 —— 所以这里用 LINQ 的 OrderBy 链（同样稳定）。
        // 末级比较用中文区域，对应 Node 的 localeCompare(..., 'zh')。
        var zh = StringComparer.Create(new CultureInfo("zh-CN"), ignoreCase: false);
        return out_
            .OrderByDescending(v => v.IsSelected)
            .ThenByDescending(v => v.Managed)
            .ThenByDescending(v => v.ModCount)
            .ThenBy(v => v.Name, zh)
            .ToList();
    }

    /// <summary>
    /// 扫描全部文件夹下的全部版本，摊平成一个列表（Node <c>scanAll</c>）。
    /// </summary>
    public static List<ClientVersion> ScanAll(IEnumerable<GameFolder> folders)
    {
        var all = new List<ClientVersion>();
        foreach (var f in folders)
        {
            foreach (var v in ScanVersions(f.Path))
            {
                all.Add(v with { FolderName = f.Name, FolderPath = f.Path, FolderSelected = f.IsSelected });
            }
        }
        return all;
    }

    // ------------------------------------------------------------------
    // 匹配
    // ------------------------------------------------------------------

    /// <summary>
    /// 给整合包挑一个最合适的目标版本（Node <c>matchTarget</c>）。
    ///
    /// <para>打分规则（能叠加）：版本名完全一致 +100 / 版本名互相包含 +40 /
    /// 原版版本号一致 +30 / 加载器一致 +15 / 之前用本工具更新过 +25 /
    /// PCL2 当前选中 +20（仅当所属文件夹也被选中）或 +8（仅版本选中）/
    /// 装了 mod +5。后三项<b>不写进 why</b>（Node 就是这么写的）：
    /// 它们是弱证据，写出来会让「自动匹配依据」一栏显得理由很硬。</para>
    ///
    /// <para><b>阈值 30 是硬编码的</b>：低于它的第一名不采纳，
    /// 宁可让用户手动选，也不要猜一个只靠弱证据撑起来的目标。</para>
    /// </summary>
    /// <returns><c>best</c> 匹配到的版本（可能为 null），<c>ranked</c> 按分数降序的全量列表。</returns>
    public static (ClientVersion? Best, List<ClientVersion> Ranked) MatchTarget(
        IReadOnlyList<ClientVersion> versions, string? packVersionName, string? packGameVersion, string? packLoader)
    {
        if (versions.Count == 0)
        {
            return (null, []);
        }

        var packName = (packVersionName ?? "").Trim().ToLowerInvariant();
        var packGame = (packGameVersion ?? "").Trim().ToLowerInvariant();
        var packLd = (packLoader ?? "").Trim().ToLowerInvariant();

        var ranked = new List<ClientVersion>();
        foreach (var v in versions)
        {
            var name = v.Name.ToLowerInvariant();
            var game = (v.GameVersion ?? "").ToLowerInvariant();
            var score = 0;
            var why = new List<string>();

            if (packName.Length > 0 && name == packName)
            {
                score += 100;
                why.Add("版本名完全一致");
            }
            else if (packName.Length > 0 && (name.Contains(packName, StringComparison.Ordinal)
                                             || packName.Contains(name, StringComparison.Ordinal)))
            {
                score += 40;
                why.Add("版本名相近");
            }

            if (packGame.Length > 0 && (game == packGame || name.Contains(packGame, StringComparison.Ordinal)))
            {
                score += 30;
                why.Add($"同为 {packGameVersion}");
            }
            if (packLd.Length > 0 && string.Equals(v.Loader, packLd, StringComparison.Ordinal))
            {
                score += 15;
                why.Add($"同为 {v.Loader}");
            }
            if (v.Managed)
            {
                score += 25;
                why.Add("之前用本工具更新过");
            }
            // 每个游戏文件夹各有各的「选中版本」，所以文件夹本身选中才更有说服力
            if (v.IsSelected && v.FolderSelected)
            {
                score += 20;
                why.Add("PCL2 当前正在用");
            }
            else if (v.IsSelected)
            {
                score += 8;
            }
            if (v.ModCount > 0)
            {
                score += 5;
            }

            ranked.Add(v with { Score = score, Why = why });
        }

        // 稳定排序：同分时保持扫描顺序（Node 的 sort 同样稳定）
        ranked = ranked.OrderByDescending(v => v.Score).ToList();

        var best = ranked[0].Score >= 30 ? ranked[0] : null;
        return (best, ranked);
    }

    // ------------------------------------------------------------------
    // 手动指定
    // ------------------------------------------------------------------

    /// <summary>
    /// 用户手动指定一个目录时，把它规整成版本对象（Node <c>describeDir</c>）。
    /// 支持直接丢 <c>versions/xxx</c> 目录，也支持丢 <c>.minecraft</c> 目录，
    /// 都不像就当成一个裸内容目录（里面直接有 <c>mods/</c>）。
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">目录不存在。</exception>
    /// <exception cref="ArgumentException">不是目录。</exception>
    /// <exception cref="InvalidOperationException">目录里没有 mods，看着不像游戏版本目录。</exception>
    public static ClientVersion DescribeDir(string dir)
    {
        var abs = Path.GetFullPath(dir);
        if (!Directory.Exists(abs) && !File.Exists(abs))
        {
            throw new DirectoryNotFoundException($"目录不存在：{abs}");
        }
        if (!Directory.Exists(abs))
        {
            throw new ArgumentException($"这不是一个目录：{abs}");
        }

        var name = Path.GetFileName(abs);
        var parent = Path.GetDirectoryName(abs) ?? "";

        // 像是 versions/<name>
        if (string.Equals(Path.GetFileName(parent), AppConfig.McVersionsDir, StringComparison.OrdinalIgnoreCase))
        {
            var mcRoot = Path.GetDirectoryName(parent) ?? "";
            var hit = ScanVersions(mcRoot).FirstOrDefault(v => v.Name == name);
            if (hit is not null)
            {
                return hit with
                {
                    FolderName = Path.GetFileName(mcRoot),
                    FolderPath = mcRoot,
                    Manual = true,
                };
            }
        }

        // 像是 .minecraft
        if (Directory.Exists(Path.Combine(abs, AppConfig.McVersionsDir)))
        {
            var list = ScanVersions(abs);
            if (list.Count > 0)
            {
                return list[0] with { FolderName = name, FolderPath = abs, Manual = true };
            }
        }

        // 什么都不像，就当成一个裸内容目录（里面直接有 mods/）
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, _, _, _) in AppConfig.ManagedCategories)
        {
            var d = Path.Combine(abs, key);
            counts[key] = key == "mods" ? CountFiles(d, IsJar) : (Directory.Exists(d) ? 1 : 0);
        }
        if (counts["mods"] == 0 && !Directory.Exists(Path.Combine(abs, "mods")))
        {
            throw new InvalidOperationException(
                $"这个目录里没有 mods 文件夹，看着不像游戏版本目录：{abs}");
        }

        return new ClientVersion
        {
            Name = name,
            VersionDir = abs,
            ContentDir = abs,
            Isolated = true,
            IsolationBasis = "manual",
            GameVersion = null,
            Loader = null,
            LoaderVersion = null,
            Info = null,
            IsSelected = false,
            ModCount = counts["mods"],
            Counts = counts,
            Managed = File.Exists(Path.Combine(abs, AppConfig.ManifestName)),
            FolderName = Path.GetFileName(Path.GetDirectoryName(abs) ?? ""),
            FolderPath = Path.GetDirectoryName(abs) ?? "",
            Manual = true,
        };
    }
}

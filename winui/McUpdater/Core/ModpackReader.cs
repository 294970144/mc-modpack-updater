using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace McUpdater.Core;

/// <summary>整合包格式标识（移植自 Node 版 <c>FORMAT</c>）。</summary>
public static class ModpackFormat
{
    /// <summary>PCL2「导出整合包」产物：外层 zip 里套一个 <c>.mrpack</c>。</summary>
    public const string Pcl2Mrpack = "pcl2-mrpack";

    /// <summary>用户直接拿到了 <c>.mrpack</c> 本体（后缀被改成 zip）。</summary>
    public const string Mrpack = "mrpack";

    /// <summary>认不出格式，靠通用内容根探测。</summary>
    public const string Generic = "generic";
}

/// <summary>内容根候选（Node: <c>detectContentRoots</c> 的返回项）。</summary>
/// <param name="Root">相对 zip 根的目录名；空串表示就在 zip 根。</param>
/// <param name="Categories">该根下命中到的受管分类目录名。</param>
/// <param name="Score">可信度评分，越大越可信。</param>
public readonly record struct ContentRootCandidate(string Root, IReadOnlyList<string> Categories, int Score);

/// <summary>分类下的一个文件条目（Node: <c>ModpackHandle.listCategory</c> 的返回项）。</summary>
/// <param name="Entry">底层 zip 条目。</param>
/// <param name="RelPath">相对分类目录的路径，如 <c>sodium.jar</c>。</param>
/// <param name="Size">解压后字节数。</param>
/// <param name="Crc32">内容 CRC32。</param>
/// <param name="Fingerprint"><c>size:crc32</c>，对比时的字节级指纹。</param>
public sealed record PackFileEntry(ZipEntry Entry, string RelPath, long Size, uint Crc32, string Fingerprint);

/// <summary>
/// 整合包读取器（移植自 Node 版 <c>src/core/modpack-reader.js</c>）。
///
/// <para><b>已知真实样本（PCL2「导出整合包」的标准产物）</b>：
/// <code>
///   1.21.11 1.0.6.zip
///     ├── modpack.mrpack            ← 真身，Modrinth 整合包格式
///     │     ├── modrinth.index.json ← 游戏版本 + 加载器版本直接白给
///     │     └── overrides/          ← 内容根，1:1 对应 versions/&lt;版本&gt;/
///     └── Plain Craft Launcher.exe  ← 启动器本体，忽略
/// </code>
/// 识别到这个格式就走快速路径：<c>overrides/</c> 直接就是内容根，零歧义。
/// 认不出来再回落到通用内容根探测。</para>
///
/// <para><b>三条内层包读取路径都必须原样保留</b>，它们决定打开速度：
/// STORED 走子区间直读（零解压零落盘）；大包落缓存临时文件（配合 LRU，
/// 同一整合包重复打开秒回，且避免 Defender 反复全量扫描）；小包才进内存。</para>
/// </summary>
public sealed class ModpackHandle : IDisposable
{
    private readonly ZipReader _reader;
    private readonly ZipReader? _outerReader;
    private readonly string? _tmpFile;
    private readonly bool _tmpFilePersistent;

    /// <summary>构造整合包句柄。除 <c>openModpack</c> 外请勿手工调用。</summary>
    public ModpackHandle(
        string zipPath,
        ZipReader reader,
        ZipReader? outerReader,
        string? tmpFile,
        bool tmpFilePersistent,
        string format,
        string contentRoot,
        string packName,
        string? packVersion,
        string? gameVersion,
        string? loader,
        string? loaderVersion,
        string? versionNameHint,
        bool? isolationHint,
        IReadOnlyList<ContentRootCandidate> rootCandidates)
    {
        ZipPath = zipPath;
        _reader = reader;
        _outerReader = outerReader;
        _tmpFile = tmpFile;
        _tmpFilePersistent = tmpFilePersistent;
        Format = format;
        ContentRoot = contentRoot;
        PackName = packName;
        PackVersion = packVersion;
        GameVersion = gameVersion;
        Loader = loader;
        LoaderVersion = loaderVersion;
        VersionNameHint = versionNameHint;
        IsolationHint = isolationHint;
        VersionName = versionNameHint;
        RootCandidates = rootCandidates;
    }

    /// <summary>整合包文件路径。</summary>
    public string ZipPath { get; }

    /// <summary>格式标识，见 <see cref="ModpackFormat"/>。</summary>
    public string Format { get; }

    /// <summary>内容根（相对 zip 根；空串表示就在 zip 根）。</summary>
    public string ContentRoot { get; }

    /// <summary>整合包名（优先取 modrinth.index.json 的 name）。</summary>
    public string PackName { get; }

    /// <summary>整合包版本（mrpack 的 versionId）。</summary>
    public string? PackVersion { get; }

    /// <summary>游戏版本。</summary>
    public string? GameVersion { get; }

    /// <summary>加载器名（Fabric/Quilt/Forge/NeoForge），识别不出为 <c>null</c>。</summary>
    public string? Loader { get; }

    /// <summary>加载器版本。</summary>
    public string? LoaderVersion { get; }

    /// <summary>PCL2 Setup.ini 里的版本名。</summary>
    public string? VersionNameHint { get; }

    /// <summary>PCL2 隔离状态（Setup.ini 的 VersionArgumentIndie == '1'）。</summary>
    public bool? IsolationHint { get; }

    /// <summary>
    /// 整合包期望的版本文件夹名，用来自动匹配本地目标目录。
    /// Node 版是 <c>versionName: versionNameHint</c> 的同义别名。
    /// </summary>
    public string? VersionName { get; }

    /// <summary>内容根候选列表（通用探测路径才有值）。</summary>
    public IReadOnlyList<ContentRootCandidate> RootCandidates { get; }

    /// <summary>各受管分类的文件数。</summary>
    public Dictionary<string, int> CategoryCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>底层 reader，供 DiffEngine 读取 jar 内容。</summary>
    public ZipReader Reader => _reader;

    /// <summary>
    /// 列出某个分类下的全部文件条目（相对内容根的路径）。
    ///
    /// <para><b>contentRoot 为空串时前缀仍是 <c>"mods/"</c> 而不是 <c>"/mods/"</c></b>：
    /// Node 写的是 <c>this.contentRoot ? ... : ...</c>，空串在 JS 里是 falsy，
    /// 所以走后者。C# 必须用 <c>string.IsNullOrEmpty</c> 而不是
    /// <c>!= null</c>，否则会在 zip 根的整合包里一个文件都列不出来。</para>
    /// </summary>
    public List<PackFileEntry> ListCategory(string categoryKey)
    {
        // ManagedCategories 是元组数组（值类型），不能用 null 判"没找到"——
        // Array.Find 未命中会返回 default 元组而不是 null。
        // 注意分类的 Key 与目录名相同（config.MANAGED_CATEGORIES 里 key === dir），
        // 所以命中后直接用 categoryKey 当目录名即可。
        var found = false;
        foreach (var c in AppConfig.ManagedCategories)
        {
            if (c.Key == categoryKey)
            {
                found = true;
                break;
            }
        }
        if (!found)
        {
            return [];
        }
        var prefix = string.IsNullOrEmpty(ContentRoot) ? $"{categoryKey}/" : $"{ContentRoot}/{categoryKey}/";

        var result = new List<PackFileEntry>();
        foreach (var e in _reader.Entries)
        {
            if (e.IsDir || e.Size <= 0)
            {
                continue;
            }
            if (!e.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            var fp = $"{e.Size}:{e.Crc32}";
            result.Add(new PackFileEntry(e, e.Name[prefix.Length..], e.Size, e.Crc32, fp));
        }
        return result;
    }

    /// <summary>读取某个条目的内容。</summary>
    public byte[] Read(ZipEntry entry) => _reader.Read(entry);

    /// <summary>按名字读取条目内容。</summary>
    public byte[] Read(string name) => _reader.Read(name);

    /// <summary>把条目解压到磁盘文件。</summary>
    public void ExtractTo(ZipEntry entry, string destPath) => _reader.ExtractToFile(entry, destPath);

    /// <summary>释放 reader；非缓存的一次性临时文件一并删除。</summary>
    public void Dispose()
    {
        try
        {
            _reader.Dispose();
        }
        catch (Exception)
        {
            // 与 Node 版一致：close 里的异常一律吞掉
        }

        if (_outerReader is not null && !ReferenceEquals(_outerReader, _reader))
        {
            try
            {
                _outerReader.Dispose();
            }
            catch (Exception)
            {
                // 同上
            }
        }

        if (_tmpFile is not null && !_tmpFilePersistent)
        {
            // 非缓存的一次性临时文件才删；命中缓存的留着复用
            try
            {
                File.Delete(_tmpFile);
            }
            catch (Exception)
            {
                // 同上
            }
        }
    }
}

/// <summary>整合包元信息 + 打开逻辑。移植自 Node 版同名模块的函数部分。</summary>
public static class ModpackReader
{
    // ------------------------------------------------------------------
    // PCL2 Setup.ini
    // ------------------------------------------------------------------

    /// <summary>
    /// 解析 PCL2 的 Setup.ini（<c>Key:Value</c> 格式）。
    ///
    /// <para><b>用 Split('\n') 而不是按行枚举</b>：Node 写的是
    /// <c>text.replace(/\r\n/g, '\n').split('\n')</c> —— 注意它只替换
    /// CRLF，<em>不处理裸 CR</em>。C# 的 <c>Replace("\r\n","\n")</c> 与之
    /// 对齐；不要"顺手"改成也替换 <c>'\r'</c>，那会改变行为。</para>
    /// </summary>
    public static Dictionary<string, string> ParseSetupIni(string text)
    {
        // 【保真】用 Ordinal 而非 OrdinalIgnoreCase：JS 对象键是大小写敏感的，
        // Node 只认 `VersionVanillaName` 这个精确拼写。用忽略大小写会让
        // `versionfabric` 也命中，把一份拼错的 Setup.ini 读成有效配置。
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var t = rawLine.Trim();
            if (t.Length == 0 || t.StartsWith(';') || t.StartsWith('#'))
            {
                continue;
            }
            var i = t.IndexOf(':');
            if (i < 0)
            {
                continue;
            }
            // 【保真】Node 不检查空键名，`": 只有冒号"` 这行会存成 out[""]="只有冒号"。
            // 这里同样照存不误 —— 下游只按已知键名取值，空键不影响任何行为，
            // 但对拍时字典内容必须一致，不能"顺手清理"。
            result[t[..i].Trim()] = t[(i + 1)..].Trim();
        }
        return result;
    }

    // ------------------------------------------------------------------
    // 通用内容根探测
    // ------------------------------------------------------------------

    /// <summary>
    /// 通用内容根探测：找出哪个目录下面挂着 mods/config/resourcepacks/shaderpacks，
    /// 返回按可信度排序的候选列表。
    /// </summary>
    public static List<ContentRootCandidate> DetectContentRoots(IReadOnlyList<ZipEntry> entries)
    {
        var managedDirs = new HashSet<string>(
            AppConfig.ManagedCategories.Select(c => c.Key), StringComparer.Ordinal);

        // parentPath -> 命中的分类名集合。Node 用 Map<string, Set<string>>，
        // 依赖插入顺序；这里用有序字典保留同样的语义（后面排序是稳定的）。
        var hits = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var e in entries)
        {
            var parts = e.Name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (!managedDirs.Contains(parts[i]))
                {
                    continue;
                }
                var parent = string.Join('/', parts.Take(i));
                if (!hits.TryGetValue(parent, out var cats))
                {
                    cats = [];
                    hits[parent] = cats;
                }
                // Node 用 Set，同一分类只记一次
                if (!cats.Contains(parts[i]))
                {
                    cats.Add(parts[i]);
                }
                break;
            }
        }

        var scored = new List<ContentRootCandidate>();
        foreach (var (root, cats) in hits)
        {
            var score = cats.Count * 10;
            if (cats.Contains("mods")) { score += 50; }               // 有 mods 才是真内容根
            if (root == "overrides") { score += 40; }
            if (root.Length == 0) { score += 30; }                    // 直接在 zip 根
            if (root == ".minecraft") { score += 25; }
            if (IsVersionsDir(root)) { score += 20; }
            if (IsDotMinecraftVersionsDir(root)) { score += 20; }
            score -= root.Split('/').Length;                          // 越浅越可信

            scored.Add(new ContentRootCandidate(root, cats, score));
        }

        // Node: sort((a,b) => b.score - a.score)。同分时 Node 的 sort 是稳定的，
        // C# 的 OrderByDescending 同样稳定，且同样保序 —— 不需要额外的次级排序键。
        return [.. scored.OrderByDescending(c => c.Score)];
    }

    /// <summary>匹配 <c>versions/&lt;版本名&gt;</c>（Node: <c>/^versions\/[^/]+$/</c>）。</summary>
    private static bool IsVersionsDir(string root)
        => root.StartsWith("versions/", StringComparison.Ordinal)
           && root.IndexOf('/', "versions/".Length) < 0
           && root.Length > "versions/".Length;

    /// <summary>匹配 <c>.minecraft/versions/&lt;版本名&gt;</c>（Node: <c>/^\.minecraft\/versions\/[^/]+$/</c>）。</summary>
    private static bool IsDotMinecraftVersionsDir(string root)
        => root.StartsWith(".minecraft/versions/", StringComparison.Ordinal)
           && root.IndexOf('/', ".minecraft/versions/".Length) < 0
           && root.Length > ".minecraft/versions/".Length;

    /// <summary>
    /// 从 modrinth.index.json 的 dependencies 提取加载器。
    ///
    /// <para><b>遍历顺序即优先级</b>：Node 里 <c>map</c> 对象的键序是
    /// fabric→quilt→forge→neoforge，先命中先返回。C# 用显式顺序的数组表达，
    /// 不要改成"并发检查取任意命中"。</para>
    /// </summary>
    public static (string? Loader, string? LoaderVersion) LoaderFromDependencies(
        IReadOnlyDictionary<string, JsonElement>? deps)
    {
        if (deps is null)
        {
            return (null, null);
        }

        (string Key, string Label)[] map =
        [
            ("fabric-loader", "Fabric"),
            ("quilt-loader", "Quilt"),
            ("forge", "Forge"),
            ("neoforge", "NeoForge"),
        ];

        foreach (var (key, label) in map)
        {
            // Node 写的是 `if (deps[k])` —— 空串/0/null 都不算命中
            if (deps.TryGetValue(key, out var v)
                && v.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                var raw = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
                if (!string.IsNullOrEmpty(raw))
                {
                    return (label, raw);
                }
            }
        }
        return (null, null);
    }

    // ------------------------------------------------------------------
    // 内层包缓存
    // ------------------------------------------------------------------

    /// <summary>缓存目录：展开过的内层 mrpack 落盘缓存，重复打开同一整合包秒回。</summary>
    private static string ModpackCacheDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcupdater-modpack-cache");
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception)
        {
            // Node: mkdirSync 失败即忽略，后面写文件时会自然报错
        }
        return dir;
    }

    /// <summary>LRU 清理缓存，只保留最近 N 份。</summary>
    private static void PruneCache(string dir, int keep = 4)
    {
        FileInfo[] files;
        try
        {
            files = [.. new DirectoryInfo(dir).GetFiles().Select(f => new FileInfo(f.FullName))];
        }
        catch (Exception)
        {
            return;
        }

        var ordered = files
            .Where(f =>
            {
                try
                {
                    f.Refresh();
                    return f.Exists;
                }
                catch (Exception)
                {
                    return false;
                }
            })
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        foreach (var f in ordered.Skip(keep))
        {
            try
            {
                f.Delete();
            }
            catch (Exception)
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// 把（压缩的）内层 mrpack 展开到缓存临时文件。
    /// 同一整合包（按 来源路径 + 条目指纹）重复打开时命中缓存秒回。
    /// </summary>
    /// <returns><c>cached=true</c> 时 tmpFile 是持久缓存，Dispose 不删。</returns>
    private static (ZipReader Reader, string TmpFile, bool Cached) OpenNestedCached(
        ZipReader outer, ZipEntry ent, string sourcePath)
    {
        var key = Sha1Hex($"{sourcePath}|{ent.Size}|{ent.Crc32}|{ent.Name}")[..16];
        var dir = ModpackCacheDir();
        var tmpFile = Path.Combine(dir, $"{key}.mrpack");

        var cached = false;
        if (File.Exists(tmpFile))
        {
            try
            {
                var st = new FileInfo(tmpFile);
                if (st.Length == ent.Size)
                {
                    // 探一下能否解析中央目录，坏缓存会被下面覆盖
                    using var probe = ZipReader.FromFile(tmpFile);
                    _ = probe.Entries;
                    cached = true;
                    try
                    {
                        File.SetLastWriteTimeUtc(tmpFile, DateTime.UtcNow);   // LRU：刷新 mtime
                    }
                    catch (Exception)
                    {
                        // ignore
                    }
                }
            }
            catch (Exception)
            {
                // 缓存损坏，下面覆盖
            }
        }

        if (!cached)
        {
            // 先写 .part 再 rename，防止中断留下半截缓存
            var part = tmpFile + ".part";
            outer.ExtractToFile(ent, part);
            File.Move(part, tmpFile, overwrite: true);
            PruneCache(dir, 4);
        }

        return (ZipReader.FromFile(tmpFile), tmpFile, cached);
    }

    private static string Sha1Hex(string s)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ------------------------------------------------------------------
    // 打开整合包
    // ------------------------------------------------------------------

    /// <summary>
    /// 打开整合包。
    /// </summary>
    /// <param name="zipPath">整合包路径。</param>
    /// <param name="onProgress">进度回调（消息, 百分比）。</param>
    public static ModpackHandle Open(string zipPath, Action<string, int>? onProgress = null)
    {
        void Report(string msg, int pct) => onProgress?.Invoke(msg, pct);

        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException($"整合包文件不存在: {zipPath}", zipPath);
        }

        Report("正在读取整合包目录…", 5);
        var outer = ZipReader.FromFile(zipPath);
        var outerEntries = outer.Entries;

        var reader = outer;
        ZipReader? outerReader = outer;
        string? tmpFile = null;
        var tmpFilePersistent = false;   // 命中缓存时 true，Dispose 不删，留给下次复用
        var format = ModpackFormat.Generic;

        // ---- 快速路径：外层套了一个 .mrpack ----
        ZipEntry? mrEnt = null;
        foreach (var e in outerEntries)
        {
            if (!e.IsDir && e.Name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            {
                mrEnt = e;
                break;
            }
        }

        if (mrEnt is not null)
        {
            format = ModpackFormat.Pcl2Mrpack;
            var ent = mrEnt;
            var sizeMb = (ent.Size / 1048576.0).ToString("0.0");

            if (ent.Method == 0)
            {
                // STORED：内层 mrpack 在外层文件里是连续裸字节，子区间直读，
                // 零解压、零临时文件、零 Defender 扫描——秒开
                Report($"发现 PCL2 整合包（内层 {sizeMb} MB，直读）…", 15);
                var dataOff = outer.DataOffset(ent);
                reader = ZipReader.FromFileRange(zipPath, dataOff, ent.Size);
            }
            else if (ent.Size >= AppConfig.InMemoryThreshold)
            {
                // 大包 + 压缩：展开到缓存临时文件，同一整合包重复打开秒回
                Report($"发现 PCL2 整合包（内层 {sizeMb} MB），正在展开…", 15);
                var r = OpenNestedCached(outer, ent, zipPath);
                reader = r.Reader;
                tmpFile = r.TmpFile;
                tmpFilePersistent = r.Cached;
            }
            else
            {
                Report($"发现 PCL2 整合包（内层 {sizeMb} MB），正在读取…", 15);
                reader = ZipReader.FromBuffer(outer.Read(ent));
            }
            _ = reader.Entries;
        }
        else if (outer.Has(AppConfig.MrpackIndex))
        {
            // 用户直接拿到了 .mrpack 本体（后缀被改成 zip）
            format = ModpackFormat.Mrpack;
        }

        // ---- 读取整合包元信息 ----
        Report("正在识别游戏版本与加载器…", 25);
        var packName = StripZipExtension(Path.GetFileName(zipPath));
        string? packVersion = null;
        string? gameVersion = null;
        string? loader = null;
        string? loaderVersion = null;
        string? contentRoot = null;
        bool? isolationHint = null;
        string? versionNameHint = null;

        if (format != ModpackFormat.Generic && reader.Has(AppConfig.MrpackIndex))
        {
            try
            {
                using var idx = ModMetadata.LooseJsonParse(reader.ReadText(AppConfig.MrpackIndex));
                var root = idx.RootElement;

                if (root.TryGetProperty("name", out var nameProp)
                    && nameProp.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(nameProp.GetString()))
                {
                    packName = nameProp.GetString()!;
                }
                // Node 是 `idx.versionId || null`：空串归一成 null，别留 ""
                if (root.TryGetProperty("versionId", out var vp) && vp.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(vp.GetString()))
                {
                    packVersion = vp.GetString();
                }

                IReadOnlyDictionary<string, JsonElement>? deps = null;
                if (root.TryGetProperty("dependencies", out var depProp)
                    && depProp.ValueKind == JsonValueKind.Object)
                {
                    deps = depProp.EnumerateObject().ToDictionary(
                        p => p.Name, p => p.Value, StringComparer.Ordinal);
                    // Node 是 `(idx.dependencies && idx.dependencies.minecraft) || null`
                    if (deps.TryGetValue("minecraft", out var mc) && mc.ValueKind == JsonValueKind.String
                        && !string.IsNullOrEmpty(mc.GetString()))
                    {
                        gameVersion = mc.GetString();
                    }
                }
                var l = LoaderFromDependencies(deps);
                loader = l.Loader;
                loaderVersion = l.LoaderVersion;
            }
            catch (Exception)
            {
                // Node: logger.warn(`modrinth.index.json 解析失败: ${e.message}`) 后继续
            }

            contentRoot = AppConfig.MrpackOverrides;
        }

        // ---- PCL/Setup.ini 补充信息（版本名 + 隔离状态） ----
        var setupPath = contentRoot is not null ? $"{contentRoot}/PCL/Setup.ini" : "PCL/Setup.ini";
        if (reader.Has(setupPath))
        {
            try
            {
                var setup = ParseSetupIni(reader.ReadText(setupPath));
                if (setup.TryGetValue("VersionVanillaName", out var vanilla) && vanilla.Length > 0)
                {
                    versionNameHint = vanilla;
                    if (gameVersion is null)
                    {
                        gameVersion = vanilla;
                    }
                }

                if (loader is null)
                {
                    // Node 是 if/else-if 链，顺序即优先级
                    if (setup.TryGetValue("VersionFabric", out var fab) && fab.Length > 0)
                    {
                        (loader, loaderVersion) = ("Fabric", fab);
                    }
                    else if (setup.TryGetValue("VersionForge", out var forg) && forg.Length > 0)
                    {
                        (loader, loaderVersion) = ("Forge", forg);
                    }
                    else if (setup.TryGetValue("VersionNeoForge", out var neo) && neo.Length > 0)
                    {
                        (loader, loaderVersion) = ("NeoForge", neo);
                    }
                    else if (setup.TryGetValue("VersionQuilt", out var quil) && quil.Length > 0)
                    {
                        (loader, loaderVersion) = ("Quilt", quil);
                    }
                }

                // VersionArgumentIndie: 1=开启隔离 2=关闭 0=跟随全局
                if (setup.TryGetValue("VersionArgumentIndie", out var indie))
                {
                    isolationHint = indie == "1";
                }
            }
            catch (Exception)
            {
                // Node: logger.warn(`Setup.ini 解析失败: ${e.message}`) 后继续
            }
        }

        // ---- 回落：通用内容根探测 ----
        var rootCandidates = new List<ContentRootCandidate>();
        if (contentRoot is null)
        {
            Report("正在探测整合包结构…", 30);
            rootCandidates = DetectContentRoots(reader.Entries);
            if (rootCandidates.Count == 0)
            {
                reader.Dispose();
                if (!ReferenceEquals(outerReader, reader))
                {
                    outerReader?.Dispose();
                }
                if (tmpFile is not null && !tmpFilePersistent)
                {
                    try { File.Delete(tmpFile); } catch (Exception) { }
                }
                throw new InvalidDataException("在整合包里没有找到 mods 文件夹，这可能不是一个有效的整合包");
            }
            contentRoot = rootCandidates[0].Root;
        }

        // ---- 统计各分类文件数 ----
        var handle = new ModpackHandle(
            zipPath, reader, outerReader, tmpFile, tmpFilePersistent, format,
            contentRoot, packName, packVersion, gameVersion, loader, loaderVersion,
            versionNameHint, isolationHint, rootCandidates);

        foreach (var c in AppConfig.ManagedCategories)
        {
            handle.CategoryCounts[c.Key] = handle.ListCategory(c.Key).Count;
        }

        Report("整合包读取完成", 35);
        return handle;
    }

    /// <summary>
    /// 去掉结尾的 <c>.zip</c>（Node: <c>path.basename(zipPath).replace(/\.zip$/i, '')</c>）。
    ///
    /// <para><b>不能用 <c>Path.GetFileNameWithoutExtension</c></b>：那会把
    /// <c>foo.tar.gz</c> 也去掉 <c>.gz</c>，而 Node 只认 <c>.zip</c> 后缀。
    /// 差异场景：把 <c>整合包.zip.zip</c> 传进来时 Node 得到 <c>整合包.zip</c>，
    /// 用无扩展名版本会得到 <c>整合包</c>，manifest 与目标目录匹配都会跑偏。</para>
    /// </summary>
    private static string StripZipExtension(string fileName)
    {
        if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return fileName[..^4];
        }
        return fileName;
    }
}
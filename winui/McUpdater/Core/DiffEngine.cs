using System.Text.Json;
using System.Text.RegularExpressions;

namespace McUpdater.Core;

/// <summary>本地 mods 目录里的一个文件（Node: <c>scanLocalMods</c> 的返回项）。</summary>
internal sealed class LocalModItem
{
    public required string FileName { get; init; }
    public required string FullPath { get; init; }
    public required long Size { get; init; }

    /// <summary>修改时间（毫秒，含小数，与 Node 的 <c>stat.mtimeMs</c> 对齐）。</summary>
    public required double MTimeMs { get; init; }

    /// <summary>玩家手动禁用的（<c>.jar.disabled</c>）。</summary>
    public required bool Disabled { get; init; }

    public uint? Crc32 { get; set; }
    public string? Fingerprint { get; set; }

    /// <summary>
    /// 暂存的文件内容，供紧随其后的元数据解析复用。
    ///
    /// <para>Node 版的 P1-7 优化：CRC 计算时已经 <c>readFileSync</c> 过一次，
    /// 若不缓存，<c>parseJar</c> 会再读一次 —— 83 个 jar 双倍 I/O。
    /// 用完必须置 null，否则整批 mod 内容会一直占着内存。</para>
    /// </summary>
    public byte[]? Buf { get; set; }
}

/// <summary>带元数据的条目（Node: <c>{item, info}</c>）。</summary>
internal sealed class KeyedItem<T>
{
    public required T Item { get; init; }
    public required ModInfo Info { get; init; }
}

/// <summary>
/// 保插入序的字符串集合，精确复刻 JS <c>Set</c> 的遍历顺序。
///
/// <para><b>不能用 <c>HashSet</c> + <c>OrderBy</c> 代替</b>：那会按哈希序或
/// 字典序输出，与 Node 的插入序不同。变更清单的展示顺序是用户可见行为，
/// 也是 Node 基准回归的比对基准，必须逐项对齐。</para>
/// </summary>
internal sealed class OrderedKeySet : IEnumerable<string>
{
    private readonly List<string> _order = [];
    private readonly HashSet<string> _seen;

    public OrderedKeySet(IEnumerable<string> initial)
    {
        _seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in initial)
        {
            Add(k);
        }
    }

    public int Count => _order.Count;

    public bool Add(string key)
    {
        if (!_seen.Add(key))
        {
            return false;
        }
        _order.Add(key);
        return true;
    }

    public IEnumerator<string> GetEnumerator() => _order.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// 元数据缓存：以指纹为 key，避免重复解析同一个 jar。
///
/// <para><b>本移植范围只做读取侧</b>：Node 版由 router 在整个 diff 结束后调
/// <c>state.cache.save()</c> 落盘，C# 侧把时机交给 UI 层决定 ——
/// <see cref="DiffEngine"/> 自身不写任何文件。这是刻意的：diff 阶段落盘会在
/// 用户只是"看一眼变更清单"时也产生副作用。</para>
/// </summary>
public sealed class MetaCache
{
    private readonly Dictionary<string, ModInfo> _entries;
    private readonly Dictionary<string, uint> _crcs;
    private readonly string? _filePath;

    /// <summary>CRC 缓存条目上限（Node 版 <c>if (crc.length &gt; 2000)</c>）。</summary>
    public const int MaxCrcEntries = 2000;

    public MetaCache(string? filePath = null)
    {
        _filePath = filePath;
        _entries = new Dictionary<string, ModInfo>(StringComparer.Ordinal);
        _crcs = new Dictionary<string, uint>(StringComparer.Ordinal);

        if (filePath is null || !File.Exists(filePath))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (root.TryGetProperty("entries", out var ent) && ent.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in ent.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    _entries[p.Name] = new ModInfo
                    {
                        Id = Str(p.Value, "id") ?? "",
                        Name = Str(p.Value, "name") ?? "",
                        Version = Str(p.Value, "version") ?? "",
                        Loader = Str(p.Value, "loader") ?? "",
                        Guessed = Bool(p.Value, "guessed") ?? false,
                        FileName = "",
                    };
                }
            }

            if (root.TryGetProperty("crc", out var crc) && crc.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in crc.EnumerateObject())
                {
                    if (p.Value.TryGetUInt32(out var v))
                    {
                        _crcs[p.Name] = v;
                    }
                }
            }
        }
        catch (Exception)
        {
            // 缓存坏了就当没有（Node: catch (_) {}）
            _entries.Clear();
            _crcs.Clear();
        }
    }

    /// <summary>CRC 缓存（供 <see cref="DiffEngine"/> 复用来跳过重复读盘）。</summary>
    internal Dictionary<string, uint> CrcMap => _crcs;

    /// <summary>取缓存的元数据，没有返回 <c>null</c>。</summary>
    public ModInfo? Get(string fingerprint)
        => _entries.TryGetValue(fingerprint, out var v) ? v : null;

    /// <summary>写入元数据缓存。</summary>
    public void Set(string fingerprint, ModInfo info)
    {
        _entries[fingerprint] = new ModInfo
        {
            Id = info.Id,
            Name = info.Name,
            Version = info.Version,
            Loader = info.Loader,
            Guessed = info.Guessed,
            FileName = info.FileName,
        };
    }

    /// <summary>元数据条目数。</summary>
    public int Count => _entries.Count;

    /// <summary>落盘。CRC 表只保留最近 2000 条，防止无限膨胀。</summary>
    /// <returns>写入是否成功。</returns>
    public bool Save()
    {
        if (_filePath is null)
        {
            return false;
        }
        try
        {
            var entries = _entries.ToDictionary(
                kv => kv.Key,
                kv => new Dictionary<string, object?>
                {
                    ["id"] = kv.Value.Id,
                    ["name"] = kv.Value.Name,
                    ["version"] = kv.Value.Version,
                    ["loader"] = kv.Value.Loader,
                    ["guessed"] = kv.Value.Guessed,
                });
            var crc = _crcs.TakeLast(MaxCrcEntries).ToDictionary(kv => kv.Key, kv => kv.Value);

            var json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["version"] = 1,
                ["updatedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["entries"] = entries,
                ["crc"] = crc,
            });
            File.WriteAllText(_filePath, json);
            return true;
        }
        catch (Exception)
        {
            // Node: logger.warn(`元数据缓存写入失败: ${e.message}`)
            return false;
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v)
           && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;
}

/// <summary>
/// 对比引擎（移植自 Node 版 <c>src/core/diff-engine.js</c>）。
///
/// <para><b>核心策略</b>：</para>
/// <list type="number">
/// <item><b>指纹预筛。</b>zip 中央目录白送每个条目的 CRC32，本地文件算一次 CRC32。
/// (size + crc32) 相同 = 字节级一致 = 必然 unchanged，直接跳过元数据解析。</item>
/// <item><b>mods 必须按 jar 内解析出的 modId 匹配，绝不能按文件名。</b>
/// PCL2 会给 mod 加中文前缀（"[钠] sodium-fabric-0.8.13.jar"），
/// 按文件名比会把同一个 mod 误判成「移除旧的 + 新增新的」。</item>
/// <item><b>modId 不保证唯一。</b>真实整合包里出现过同一 modId 有两个 jar
/// （sodium 0.8.12 与 0.8.13 并存），这会让 Fabric 直接崩游戏。
/// 必须识别成 conflict 并显式告警，而不是默默取一个。</item>
/// <item><b>解析不出 id 的 mod（guessed=true）id 不可信，降级用文件名匹配。</b></item>
/// </list>
/// </summary>
public static class DiffEngine
{
    /// <summary>保护规则编译缓存（Node: <c>matcherCache</c>）。</summary>
    private static readonly Dictionary<string, Func<string, bool>> ProtectedMatchers =
        new(StringComparer.Ordinal);

    private static readonly Regex JarFilter =
        new(@"\.jar(\.disabled)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 文件名里的「复制」标记：Windows 中文是「 - 副本」，英文是「 - Copy」，
    /// 还有「(1)」（Node: <c>COPY_MARKER</c>）。
    /// </summary>
    private static readonly Regex CopyMarker =
        new(@"副本|copy|\(\d+\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 资源包/光影包只认 .zip（Iris/OptiFine 光影包和资源包的标准格式），
    /// 避免把说明文档（.txt）、下载残留、.disabled 等当成资源包。
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedExts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["resourcepacks"] = [".zip"],
            ["shaderpacks"] = [".zip"],
        };

    // ------------------------------------------------------------------
    // 基础工具
    // ------------------------------------------------------------------

    /// <summary>归一化版本串，用于「版本号相同但文件不同」的判断。</summary>
    private static string NormVersion(string? v) => (v ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// 语义化版本粗比较，返回 1 / 0 / -1。
    ///
    /// <para><b>必须复刻 JS 的 <c>parseInt</c>，不能用 <c>int.TryParse</c></b>。
    /// 两者在真实 mod 版本串上会分叉：<c>parseInt("12abc")</c> = <b>12</b>，
    /// 而 <c>int.TryParse("12abc")</c> 失败 → 两边都退化成 -1，比较结果翻转。
    /// 版本串里出现 <c>1.0.0-fabric.2</c> 这类时，分段后每段未必是纯数字，
    /// 行为必须和 Node 一致，否则升级/降级判断会反。</para>
    ///
    /// <para>Node 还把 NaN 当 -1（非数字段视为"比任何数字都小"）；
    /// 越界段（JS 里是 <c>undefined</c>）同样走 parseInt(undefined) = NaN = -1。</para>
    /// </summary>
    public static int CompareVersion(string? a, string? b)
    {
        var pa = SplitVersion(a);
        var pb = SplitVersion(b);

        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var va = i < pa.Length ? JsParseIntOrNegOne(pa[i]) : -1d;
            var vb = i < pb.Length ? JsParseIntOrNegOne(pb[i]) : -1d;
            if (va != vb)
            {
                return va > vb ? 1 : -1;
            }
        }
        return 0;
    }

    private static string[] SplitVersion(string? v)
        => (v ?? "").Split(['.', '-', '+', '_'], StringSplitOptions.RemoveEmptyEntries);

    private static double JsParseIntOrNegOne(string s)
    {
        var i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i]))
        {
            i++;
        }

        var sign = 1d;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
        {
            if (s[i] == '-')
            {
                sign = -1;
            }
            i++;
        }

        var start = i;
        double val = 0;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9')
        {
            // JS parseInt 用 double，超大数字会损失精度；这里同样用 double 保持一致
            val = val * 10 + (s[i] - '0');
            i++;
        }

        // 一位有效数字都没吃到 → NaN → Node 归一成 -1
        return i == start ? -1d : sign * val;
    }

    /// <summary>取路径末段（Node 的 <c>path.basename</c>，兼容两种分隔符）。</summary>
    private static string BaseName(string p)
    {
        var i = p.LastIndexOfAny(['/', '\\']);
        return i < 0 ? p : p[(i + 1)..];
    }

    /// <summary>
    /// 取扩展名含点号（Node 的 <c>path.extname</c>）。
    ///
    /// <para><b>点开头文件返回空串</b>（<c>.hidden</c> → <c>""</c>）：Node 明确排除
    /// basename 首个字符就是点的情况。若这里图省事用 <c>Path.GetExtension</c>，
    /// <c>.zip</c> 这种隐藏文件会被判成合法资源包、把整合包里的垃圾文件
    /// 当成资源包列进变更清单。</para>
    /// </summary>
    private static string ExtName(string p)
    {
        var i = p.LastIndexOfAny(['/', '\\']);
        var baseName = i < 0 ? p : p[(i + 1)..];
        var dot = baseName.LastIndexOf('.');
        return dot <= 0 ? "" : baseName[dot..];
    }

    /// <summary>取分类的保护规则匹配器，编译一次并缓存。</summary>
    public static Func<string, bool> GetProtectedMatcher(string categoryKey)
    {
        lock (ProtectedMatchers)
        {
            if (!ProtectedMatchers.TryGetValue(categoryKey, out var m))
            {
                m = FsHelpers.MakeMatcher(AppConfig.GetProtectedPatterns(categoryKey));
                ProtectedMatchers[categoryKey] = m;
            }
            return m;
        }
    }

    // ------------------------------------------------------------------
    // 本地扫描
    // ------------------------------------------------------------------

    /// <summary>
    /// 扫描本地 mods 目录，产出带指纹的条目列表。
    ///
    /// <para><b>刻意不排序 —— 直接沿用 <c>Directory.EnumerateFiles</c> 的原生顺序</b>。
    /// 这个顺序会一路传到变更清单的展示顺序（本地独有的 key 按它插入
    /// <c>allKeys</c>），所以它不是"内部实现细节"，而是用户可见行为。
    /// 真实样本实测：Node 的 <c>fs.readdirSync</c> 给出的是按文件名的顺序，
    /// 例如 <c>farsight-fabric-...jar</c>（modId <c>holdthatchunk</c>）排在
    /// <c>fsb-interop-...jar</c> 之前 —— <b>不是按 modId 字母序</b>。
    /// 一旦"顺手"加个 <c>OrderBy</c>，整个移除项区块的顺序就与原版不一致。</para>
    ///
    /// <para>Node 侧同一份代码在不同文件系统上的顺序确实可能不同（NTFS 与
    /// FAT32 的目录项排序规则不一样）。但既然目标是行为保真，就照抄原版做法，
    /// 不在这里"修正"。</para>
    /// </summary>
    public static List<string> ScanLocalModsPublic(string modsDir)
        => [.. ScanLocalMods(modsDir).Select(m => m.FileName)];

    internal static List<LocalModItem> ScanLocalMods(string modsDir)
    {
        var result = new List<LocalModItem>();
        if (!Directory.Exists(modsDir))
        {
            return result;
        }

        foreach (var path in Directory.EnumerateFiles(modsDir))
        {
            var name = Path.GetFileName(path);
            var lower = name.ToLowerInvariant();

            // .disabled 是玩家手动禁用的，视为存在但标记出来
            if (!lower.EndsWith(".jar", StringComparison.Ordinal)
                && !lower.EndsWith(".jar.disabled", StringComparison.Ordinal))
            {
                continue;
            }

            FileInfo st;
            try
            {
                st = new FileInfo(path);
                st.Refresh();
                if (!st.Exists)
                {
                    continue;
                }
            }
            catch (Exception)
            {
                continue;
            }

            result.Add(new LocalModItem
            {
                FileName = name,
                FullPath = path,
                Size = st.Length,
                MTimeMs = (double)st.LastWriteTimeUtc.Ticks / TimeSpan.TicksPerMillisecond,
                Disabled = lower.EndsWith(".disabled", StringComparison.Ordinal),
            });
        }

        return result;
    }

    /// <summary>
    /// 按需补算本地文件 CRC32。缓存键是 <c>路径|大小|修改时间</c>。
    ///
    /// <para><b>修改时间的取整必须用 <c>Math.Floor(x + 0.5)</c></b>：Node 的
    /// <c>Math.round</c> 是「四舍五入到 +∞」，而 .NET 的 <c>Math.Round</c> 默认是
    /// <em>银行家舍入</em>（0.5 取偶）。两者在 <c>.5</c> 边界上分叉，一旦分叉，
    /// 同一个文件两次对比会算出两个不同的缓存键，缓存永久失效。</para>
    /// </summary>
    private static string EnsureLocalFingerprint(LocalModItem item, Dictionary<string, uint>? crcCache)
    {
        if (item.Fingerprint is not null)
        {
            return item.Fingerprint;
        }

        var ck = $"{item.FullPath.ToLowerInvariant()}|{item.Size}|{JsRound(item.MTimeMs)}";
        if (crcCache is not null && crcCache.TryGetValue(ck, out var hit))
        {
            item.Crc32 = hit;
            item.Fingerprint = $"{item.Size}:{hit}";
            return item.Fingerprint;
        }

        try
        {
            var buf = File.ReadAllBytes(item.FullPath);
            var crc = FsHelpers.Crc32(buf);
            item.Crc32 = crc;
            item.Fingerprint = $"{item.Size}:{crc}";
            item.Buf = buf;   // 暂存给元数据解析复用
            crcCache?[ck] = crc;
        }
        catch (Exception)
        {
            item.Fingerprint = $"{item.Size}:err";
        }
        return item.Fingerprint!;
    }

    private static long JsRound(double v) => (long)Math.Floor(v + 0.5);

    /// <summary>
    /// 多份里选一份作为「代表」（Node 的 <c>pick</c>）。
    ///
    /// <para>排序三级：版本高的优先；版本相同则非副本优先（避免把玩家复制的
    /// 「xxx - 副本」当原版保留、反而把真原版标成多余）；都非副本时文件名短的优先。
    /// 完全并列时取先出现的那个 —— Node 的 <c>Array.sort</c> 自 ES2019 起保证稳定，
    /// 这里用单趟取最小值复刻同样的"稳定取第一个"语义。</para>
    ///
    /// <para><b>实现为类级泛型方法而非局部函数</b>：局部静态函数无法引用外层
    /// 方法的类型参数，而 <c>pick</c> 需要同时作用于 zip 侧与本地侧两种条目类型。</para>
    /// </summary>
    private static KeyedItem<T> Pick<T>(List<KeyedItem<T>> group) where T : notnull
    {
        var best = group[0];
        for (var i = 1; i < group.Count; i++)
        {
            if (PickCompare(best, group[i]) > 0)
            {
                best = group[i];
            }
        }
        return best;
    }

    /// <summary>返回负数表示 <paramref name="a"/> 更该当代表。</summary>
    private static int PickCompare<T>(KeyedItem<T> a, KeyedItem<T> b) where T : notnull
    {
        // 版本高的优先 → 比较 b 与 a（与 Node 的 sort(a,b) => compareVersion(b,a) 同向）
        var cv = CompareVersion(b.Info.Version, a.Info.Version);
        if (cv != 0)
        {
            return cv;
        }
        var ac = IsCopyName(NameOf(a.Item)) ? 1 : 0;
        var bc = IsCopyName(NameOf(b.Item)) ? 1 : 0;
        if (ac != bc)
        {
            return ac - bc;
        }
        // 文件名短的优先
        return NameOf(a.Item).Length.CompareTo(NameOf(b.Item).Length);
    }

    private static bool IsCopyName(string name) => CopyMarker.IsMatch(name);

    /// <summary>统一取出条目的展示文件名（zip 侧是 relPath，本地侧是 fileName）。</summary>
    private static string NameOf<T>(T item) where T : notnull
        => item switch
        {
            PackFileEntry p => BaseName(p.RelPath),
            LocalModItem l => l.FileName,
            _ => throw new InvalidOperationException("未知条目类型"),
        };

    // ------------------------------------------------------------------
    // diff: mods
    // ------------------------------------------------------------------

    /// <summary>
    /// 对比 mods。
    /// </summary>
    /// <param name="zipMods">来自 <c>pack.ListCategory("mods")</c>。</param>
    /// <param name="localModsDir">本地 mods 目录。</param>
    /// <param name="pack">整合包句柄，用于读取 jar 内容。</param>
    /// <param name="manifest">上次安装记录，可为 <c>null</c>。</param>
    /// <param name="cache">元数据缓存，可为 <c>null</c>。</param>
    /// <param name="onProgress">进度回调。</param>
    public static DiffResult DiffMods(
        IReadOnlyList<PackFileEntry> zipMods,
        string localModsDir,
        ModpackHandle pack,
        Manifest? manifest,
        MetaCache? cache,
        Action<string, int>? onProgress = null)
    {
        void Report(string msg, int pct) => onProgress?.Invoke(msg, pct);

        var zipItems = zipMods.Where(m => JarFilter.IsMatch(m.RelPath)).ToList();
        var localItems = ScanLocalMods(localModsDir);
        cache ??= new MetaCache();

        // ---------- 阶段 1：指纹预筛 ----------
        Report("正在比对文件指纹…", 45);

        var zipByFp = new Dictionary<string, List<PackFileEntry>>(StringComparer.Ordinal);
        foreach (var z in zipItems)
        {
            if (!zipByFp.TryGetValue(z.Fingerprint, out var list))
            {
                list = [];
                zipByFp[z.Fingerprint] = list;
            }
            list.Add(z);
        }

        // 只对「大小能在 zip 侧找到对应」的本地文件算 CRC，其余必然是差异项
        var zipSizes = new HashSet<long>(zipItems.Select(z => z.Size));
        var identicalPairs = new List<(PackFileEntry Zip, LocalModItem Local)>();
        var localRest = new List<LocalModItem>();
        var paired = new HashSet<PackFileEntry>(ReferenceEqualityComparer.Instance);
        var crcCache = cache.CrcMap;

        foreach (var l in localItems)
        {
            if (!zipSizes.Contains(l.Size))
            {
                localRest.Add(l);
                continue;
            }

            var fp = EnsureLocalFingerprint(l, crcCache);
            if (zipByFp.TryGetValue(fp, out var cand) && cand.Count > 0)
            {
                // Node: cand.find(c => !c._paired) || cand[0]
                var z = cand.FirstOrDefault(c => !paired.Contains(c)) ?? cand[0];
                paired.Add(z);
                identicalPairs.Add((z, l));
            }
            else
            {
                localRest.Add(l);
            }
        }

        var zipRest = zipItems.Where(z => !paired.Contains(z)).ToList();

        // ---------- 阶段 2：解析元数据（只解析必要的） ----------
        Report("正在解析模组元数据…", 55);

        // 完全一致的那批也要拿 id（为了重复检测和 manifest），走缓存命中率极高
        var zipInfo = new Dictionary<PackFileEntry, ModInfo>(ReferenceEqualityComparer.Instance);
        var localInfo = new Dictionary<LocalModItem, ModInfo>(ReferenceEqualityComparer.Instance);

        foreach (var (zip, local) in identicalPairs)
        {
            var info = ResolveZip(zip, pack, cache);
            zipInfo[zip] = info;
            localInfo[local] = WithFileName(info, local.FileName);
        }
        foreach (var z in zipRest)
        {
            zipInfo[z] = ResolveZip(z, pack, cache);
        }
        foreach (var l in localRest)
        {
            localInfo[l] = ResolveLocal(l, cache);
        }

        // ---------- 阶段 3：按 modId 建索引 ----------
        Report("正在计算差异…", 70);

        // 解析成功的用 modId，兜底猜的用 "file:文件名" 以免误匹配
        var zipByKey = new Dictionary<string, List<KeyedItem<PackFileEntry>>>(StringComparer.Ordinal);
        foreach (var z in zipItems)
        {
            var k = KeyOf(zipInfo[z], BaseName(z.RelPath));
            if (!zipByKey.TryGetValue(k, out var list))
            {
                list = [];
                zipByKey[k] = list;
            }
            list.Add(new KeyedItem<PackFileEntry> { Item = z, Info = zipInfo[z] });
        }

        var localByKey = new Dictionary<string, List<KeyedItem<LocalModItem>>>(StringComparer.Ordinal);
        foreach (var l in localItems)
        {
            var k = KeyOf(localInfo[l], l.FileName);
            if (!localByKey.TryGetValue(k, out var list))
            {
                list = [];
                localByKey[k] = list;
            }
            list.Add(new KeyedItem<LocalModItem> { Item = l, Info = localInfo[l] });
        }

        // ---------- 阶段 4：产出变更 ----------
        var changes = new List<ChangeItem>();
        var conflicts = new List<DiffConflict>();

        var manifestFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifestIds = new HashSet<string>(StringComparer.Ordinal);
        if (manifest?.Mods is { Count: > 0 } mmods)
        {
            foreach (var m in mmods)
            {
                if (!string.IsNullOrEmpty(m.FileName))
                {
                    manifestFiles.Add(m.FileName.ToLowerInvariant());
                }
                if (!string.IsNullOrEmpty(m.Id))
                {
                    manifestIds.Add(m.Id);
                }
            }
        }
        var hasManifest = manifest?.Mods is { Count: > 0 };

        // 同一 key 在同一侧出现多次 = 重复安装，Fabric/Forge 会直接拒绝启动
        foreach (var (k, g) in zipByKey)
        {
            if (g.Count > 1)
            {
                conflicts.Add(RecordConflict("zip", k, g));
            }
        }
        foreach (var (k, g) in localByKey)
        {
            if (g.Count > 1)
            {
                conflicts.Add(RecordConflict("local", k, g));
            }
        }

        // 【保真】JS `new Set([...zipByKey.keys(), ...localByKey.keys()])` 保插入序：
        // 先 zip 侧全部 key，再补本地侧独有的 key。C# 用 OrderedKeySet 精确复刻，
        // 不能改成字典序或按类型分组 —— 那会让 UI 展示顺序与 Node 版不一致。
        var allKeys = new OrderedKeySet(zipByKey.Keys);
        foreach (var k in localByKey.Keys)
        {
            allKeys.Add(k);
        }

        foreach (var key in allKeys)
        {
            zipByKey.TryGetValue(key, out var zg);
            localByKey.TryGetValue(key, out var lg);

            // --- 新增 ---
            if (zg is not null && lg is null)
            {
                var z = Pick(zg);
                changes.Add(new ChangeItem
                {
                    Type = ChangeType.Added,
                    Category = "mods",
                    Key = key,
                    ModId = z.Info.Id,
                    DisplayName = Fallback(z.Info.Name, z.Info.Id),
                    FileName = BaseName(z.Item.RelPath),
                    NewVersion = z.Info.Version,
                    OldVersion = null,
                    Size = z.Item.Size,
                    Loader = z.Info.Loader,
                    Checked = true,
                    ZipRelPath = z.Item.RelPath,
                    Duplicated = zg.Count > 1,
                });
                continue;
            }

            // --- 移除 ---
            if (zg is null && lg is not null)
            {
                var l = Pick(lg);
                // 智能判断：能在上次安装记录里找到 → 整合包装的，默认勾选删除
                //           找不到 → 玩家自己装的，默认不勾并打标签
                bool? playerInstalled = !hasManifest
                    ? null     // 首次运行，无从判断
                    : !(manifestIds.Contains(l.Info.Id) || manifestFiles.Contains(NameOf(l.Item)));

                changes.Add(new ChangeItem
                {
                    Type = ChangeType.Removed,
                    Category = "mods",
                    Key = key,
                    ModId = l.Info.Id,
                    DisplayName = Fallback(l.Info.Name, l.Info.Id),
                    FileName = NameOf(l.Item),
                    OldVersion = l.Info.Version,
                    NewVersion = null,
                    Size = l.Item.Size,
                    Loader = l.Info.Loader,
                    Checked = hasManifest ? !playerInstalled!.Value : false,
                    PlayerInstalled = playerInstalled,
                    LocalPath = l.Item.FullPath,
                    Disabled = l.Item.Disabled,
                    Duplicated = lg.Count > 1,
                });
                continue;
            }

            // --- 两边都有：更新 or 无变化 ---
            var z2 = Pick(zg!);
            var l2 = Pick(lg!);
            var sameVersion = NormVersion(z2.Info.Version) == NormVersion(l2.Info.Version);
            var sameBytes = z2.Item.Fingerprint == l2.Item.Fingerprint;

            if (sameVersion && sameBytes)
            {
                changes.Add(new ChangeItem
                {
                    Type = ChangeType.Unchanged,
                    Category = "mods",
                    Key = key,
                    ModId = z2.Info.Id,
                    DisplayName = Fallback(z2.Info.Name, z2.Info.Id),
                    FileName = BaseName(z2.Item.RelPath),
                    OldVersion = l2.Info.Version,
                    NewVersion = z2.Info.Version,
                    Size = z2.Item.Size,
                    Checked = false,
                    // 内容一致但文件名不同（PCL2 给 mod 加了中文前缀）—— 不算变更，但值得提示
                    Renamed = NameOf(z2.Item) != NameOf(l2.Item),
                    OldFileName = NameOf(l2.Item),
                });
                continue;
            }

            var cmp = CompareVersion(z2.Info.Version, l2.Info.Version);
            changes.Add(new ChangeItem
            {
                Type = ChangeType.Updated,
                Category = "mods",
                Key = key,
                ModId = z2.Info.Id,
                DisplayName = Fallback(z2.Info.Name, z2.Info.Id),
                FileName = NameOf(z2.Item),
                OldFileName = NameOf(l2.Item),
                OldVersion = l2.Info.Version,
                NewVersion = z2.Info.Version,
                Size = z2.Item.Size,
                Loader = z2.Info.Loader,
                Checked = true,
                // 版本号一样但内容不同：多半是服主重新打包/热修
                Reason = sameVersion ? ChangeReason.Content
                    : (cmp < 0 ? ChangeReason.Downgrade : ChangeReason.Version),
                Downgrade = cmp < 0,
                ZipRelPath = z2.Item.RelPath,
                LocalPath = l2.Item.FullPath,
                Duplicated = zg!.Count > 1 || lg!.Count > 1,
            });
        }

        // ---------- 阶段 5：本地重复 mod 进移除列表 ----------
        // 同一 modId 在本地装了多份（Fabric/Forge 会拒绝启动），保留版本最高的，
        // 其余作为「移除」项列出，标 Duplicate=true，默认不勾，让玩家自己决定删哪份。
        // 注意：只处理 local 侧。zip 侧重复是服主打包事故，删了本地下次更新还会装回来，
        // 所以 zip 侧重复只走 conflicts 告警，不产出移除项。
        foreach (var (key, lg) in localByKey)
        {
            if (lg.Count <= 1)
            {
                continue;
            }
            var rep = Pick(lg);
            foreach (var g in lg)
            {
                if (ReferenceEquals(g, rep))
                {
                    continue;
                }
                var dupFileName = NameOf(g.Item);
                changes.Add(new ChangeItem
                {
                    Type = ChangeType.Removed,
                    Category = "mods",
                    // key 加 dup:: 前缀和文件名，避免和主变更撞 key
                    Key = $"dup::{key}::{dupFileName.ToLowerInvariant()}",
                    ModId = g.Info.Id,
                    DisplayName = Fallback(g.Info.Name, g.Info.Id) + "（重复）",
                    FileName = dupFileName,
                    OldVersion = g.Info.Version,
                    NewVersion = null,
                    Size = g.Item.Size,
                    Loader = g.Info.Loader,
                    Checked = false,            // 不自动勾，玩家自己决定
                    PlayerInstalled = null,     // 重复项无法判断归属
                    LocalPath = g.Item.FullPath,
                    Disabled = g.Item.Disabled,
                    Duplicate = true,
                    DuplicateOf = NameOf(rep.Item),
                    DuplicateOfVersion = rep.Info.Version,
                    Duplicated = true,
                });
            }
        }

        return new DiffResult { Changes = changes, Conflicts = conflicts };
    }

    /// <summary>空串回落：Node 写的是 <c>info.name || info.id</c> / <c>name || key</c>。</summary>
    private static string Fallback(string? name, string fallback)
        => string.IsNullOrEmpty(name) ? fallback : name;

    /// <summary>
    /// 索引键：解析成功的用 modId，兜底猜的用 <c>file:文件名</c> 以免误匹配。
    /// </summary>
    private static string KeyOf(ModInfo info, string fileName)
        => info.Guessed ? $"file:{fileName.ToLowerInvariant()}" : $"id:{info.Id}";

    /// <summary>
    /// 记一条重复安装冲突（Node: <c>recordConflict</c>）。
    ///
    /// <para><b>displayName 取的是 group[0]</b>：Node 原样如此，
    /// 即使多份里版本最高的那份不是第一份，它仍然用第一份的名字当标题。</para>
    /// </summary>
    private static DiffConflict RecordConflict<T>(string side, string key, List<KeyedItem<T>> group)
        where T : notnull
        => new()
        {
            Side = side,
            Key = key,
            DisplayName = Fallback(group[0].Info.Name, key),
            Files = [.. group.Select(g => new DiffConflictFile(NameOf(g.Item), g.Info.Version))],
        };

    private static ModInfo ResolveZip(PackFileEntry z, ModpackHandle pack, MetaCache cache)
    {
        var fileName = BaseName(z.RelPath);
        var cached = cache.Get(z.Fingerprint);
        if (cached is not null)
        {
            return WithFileName(cached, fileName);
        }
        var info = ModMetadata.ParseJar(pack.Read(z.Entry), fileName);
        cache.Set(z.Fingerprint, info);
        return info;
    }

    private static ModInfo ResolveLocal(LocalModItem l, MetaCache cache)
    {
        EnsureLocalFingerprint(l, cache.CrcMap);
        var cached = cache.Get(l.Fingerprint!);
        if (cached is not null)
        {
            l.Buf = null;
            return WithFileName(cached, l.FileName);
        }
        // 复用 CRC 计算时暂存的 buf，避免二次读盘
        var buf = l.Buf ?? File.ReadAllBytes(l.FullPath);
        l.Buf = null;   // 用完释放，不长期占内存
        var info = ModMetadata.ParseJar(buf, l.FileName);
        cache.Set(l.Fingerprint!, info);
        return info;
    }

    private static ModInfo WithFileName(ModInfo info, string fileName)
        => new()
        {
            Id = info.Id,
            Name = info.Name,
            Version = info.Version,
            Loader = info.Loader,
            Guessed = info.Guessed,
            FileName = fileName,
            Error = info.Error,
        };

    // ------------------------------------------------------------------
    // diff: config / resourcepacks / shaderpacks
    // ------------------------------------------------------------------

    /// <summary>
    /// 对比普通文件类目录（config / resourcepacks / shaderpacks）。
    /// 无元数据可用，按相对路径 + 指纹对比。
    /// </summary>
    public static DiffResult DiffFiles(
        string categoryKey,
        IReadOnlyList<PackFileEntry> zipItems,
        string localDir,
        Manifest? manifest)
    {
        var changes = new List<ChangeItem>();
        var localFiles = FsHelpers.ListFilesRecursive(localDir);

        // 分类内部的保护规则：mod 运行时生成的玩家数据（存档备份、查询历史、缓存…）
        // 混在 config 里，这些一律不参与对比，也不显示
        var isProtected = GetProtectedMatcher(categoryKey);
        var maxSize = AppConfig.GetMaxFileSize(categoryKey);
        AllowedExts.TryGetValue(categoryKey, out var allowedExt);   // null 表示不限制扩展名

        var skipped = new SkipCounter();

        var keepZip = zipItems.Where(z =>
        {
            // 拒绝含 .. 的路径，防整合包构造穿越攻击覆盖分类目录之外的文件
            if (FsHelpers.HasTraversal(z.RelPath)) { skipped.Note("protected", z.RelPath); return false; }
            if (isProtected(z.RelPath)) { skipped.Note("protected", z.RelPath); return false; }
            if (z.Size > maxSize) { skipped.Note("oversize", z.RelPath); return false; }
            if (allowedExt is not null && !allowedExt.Contains(ExtName(z.RelPath).ToLowerInvariant()))
            {
                skipped.Note("ext", z.RelPath);
                return false;
            }
            return true;
        }).ToList();

        var keepLocal = localFiles.Where(f =>
        {
            if (FsHelpers.HasTraversal(f)) { skipped.Note("protected", f); return false; }
            if (isProtected(f)) { skipped.Note("protected", f); return false; }
            if (allowedExt is not null && !allowedExt.Contains(ExtName(f).ToLowerInvariant()))
            {
                skipped.Note("ext", f);
                return false;
            }
            return true;
        }).ToList();

        // 后写覆盖先写（Node 的 Map 语义），且不改变首次出现的位置
        var zipMap = new Dictionary<string, PackFileEntry>(StringComparer.Ordinal);
        foreach (var z in keepZip)
        {
            zipMap[z.RelPath.ToLowerInvariant()] = z;
        }
        var localMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in keepLocal)
        {
            localMap[f.ToLowerInvariant()] = f;
        }

        var manifestSet = new HashSet<string>(StringComparer.Ordinal);
        if (manifest?.Files.TryGetValue(categoryKey, out var mfiles) == true)
        {
            foreach (var f in mfiles)
            {
                manifestSet.Add((f.Path ?? "").ToLowerInvariant());
            }
        }
        // 【注意】这里的 hasManifest 判据是「该分类有记录」，
        // 与 mods 侧的「整个 mods 列表非空」判据不同 —— 是 Node 版两处代码的原样差异，不要统一
        var hasManifest = manifestSet.Count > 0;

        var allKeys = new OrderedKeySet(zipMap.Keys);
        foreach (var k in localMap.Keys)
        {
            allKeys.Add(k);
        }

        foreach (var k in allKeys)
        {
            zipMap.TryGetValue(k, out var z);
            localMap.TryGetValue(k, out var lRel);

            if (z is not null && lRel is null)
            {
                changes.Add(new ChangeItem
                {
                    Type = ChangeType.Added,
                    Category = categoryKey,
                    Key = k,
                    DisplayName = z.RelPath,
                    FileName = z.RelPath,
                    Size = z.Size,
                    Checked = true,
                    ZipRelPath = z.RelPath,
                });
                continue;
            }

            if (z is null && lRel is not null)
            {
                var full = Path.Combine(localDir, lRel);
                long lSize = 0;
                try
                {
                    var st = new FileInfo(full);
                    lSize = st.Exists ? st.Length : 0;
                }
                catch (Exception)
                {
                    // 读不到就当 0
                }

                // 本地这边也挡一道：config 里的超大文件多半是数据不是配置，不给删
                if (lSize > maxSize)
                {
                    skipped.Note("oversize", lRel);
                    continue;
                }

                changes.Add(new ChangeItem
                {
                    Type = ChangeType.Removed,
                    Category = categoryKey,
                    Key = k,
                    DisplayName = lRel,
                    FileName = lRel,
                    Size = lSize,
                    // Node: checked: false —— 普通文件的移除项永远不自动勾
                    Checked = false,
                    PlayerInstalled = hasManifest ? !manifestSet.Contains(k) : null,
                    LocalPath = full,
                });
                continue;
            }

            // 两边都有：比指纹
            var fullPath = Path.Combine(localDir, lRel!);
            long lSize2 = -1;
            uint? lCrc = null;
            try
            {
                var st = new FileInfo(fullPath);
                if (st.Exists)
                {
                    lSize2 = st.Length;
                    if (lSize2 == z!.Size)
                    {
                        lCrc = FsHelpers.Crc32(File.ReadAllBytes(fullPath));
                    }
                }
            }
            catch (Exception)
            {
                // ignore
            }

            var same = lSize2 == z!.Size && lCrc == z.Crc32;
            changes.Add(new ChangeItem
            {
                Type = same ? ChangeType.Unchanged : ChangeType.Updated,
                Category = categoryKey,
                Key = k,
                DisplayName = z.RelPath,
                FileName = z.RelPath,
                Size = z.Size,
                Reason = ChangeReason.Content,
                // 配置文件玩家经常自己调参，覆盖会丢设置 —— 默认不勾
                Checked = !same && !string.Equals(categoryKey, "config", StringComparison.Ordinal),
                ZipRelPath = z.RelPath,
                LocalPath = fullPath,
            });
        }

        return new DiffResult { Changes = changes, Skipped = skipped.ToResult() };
    }

    /// <summary>跳过计数器（Node: <c>skipped = {protected, oversize, ext, samples}</c>）。</summary>
    private sealed class SkipCounter
    {
        private int _protected;
        private int _oversize;
        private int _ext;
        private readonly List<string> _samples = [];

        /// <summary>样本上限 6（Node 里同样是 6）。</summary>
        public void Note(string why, string path)
        {
            switch (why)
            {
                case "protected": _protected++; break;
                case "oversize": _oversize++; break;
                case "ext": _ext++; break;
            }
            if (_samples.Count < 6)
            {
                _samples.Add(path);
            }
        }

        public DiffSkipped ToResult() => new()
        {
            Protected = _protected,
            Oversize = _oversize,
            Ext = _ext,
            Samples = [.. _samples],
        };
    }
}
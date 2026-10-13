using System.Text.Json;

namespace McUpdater.Core;

/// <summary>安装记录里的一条 mod（Node: <c>mods[]</c> 元素）。</summary>
public sealed class ManifestMod
{
    /// <summary>modId（可能为空，取不到就只靠文件名判断归属）。</summary>
    public string? Id { get; init; }

    /// <summary>文件名。</summary>
    public string? FileName { get; init; }

    /// <summary>版本号。</summary>
    public string? Version { get; init; }

    /// <summary>是否为玩家自己装的（<c>build</c> 时记录，本次范围未移植）。</summary>
    public bool? PlayerInstalled { get; init; }
}

/// <summary>安装记录里的一条普通文件（Node: <c>files[category][]</c> 元素）。</summary>
public sealed class ManifestFile
{
    /// <summary>相对分类目录的路径。</summary>
    public string? Path { get; init; }

    /// <summary>是否为玩家自己装的。</summary>
    public bool? PlayerInstalled { get; init; }
}

/// <summary>
/// 安装记录（manifest）—— <b>只读侧</b>。
///
/// <para><b>它存在的唯一理由</b>：区分「这个 mod 是上次整合包装的」还是
/// 「玩家自己装的」。玩家常自己加小地图、背包整理这类客户端 mod，整合包里没有；
/// 如果「移除项」无脑默认勾选，一次更新就会把玩家自己装的东西全删光。</para>
///
/// <para><b>范围说明</b>：只移植 Node 版 <c>manifest.js</c> 的
/// <c>manifestPath</c> 与 <c>load</c>。<c>build</c> / <c>save</c> /
/// <c>projectModsState</c> / <c>projectFilesState</c> 属于 apply 链路，
/// 按约定<em>不移植</em> —— 本工具当前只做到「变更清单」，不做应用。</para>
///
/// <para>文件落在目标版本目录下，跟着这份客户端走。</para>
/// </summary>
public sealed class Manifest
{
    /// <summary>manifest 里的分类文件表（key 是分类名）。</summary>
    public Dictionary<string, List<ManifestFile>> Files { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>上次安装的 mod 清单。</summary>
    public IReadOnlyList<ManifestMod> Mods { get; init; } = [];

    /// <summary>整合包名。</summary>
    public string? PackName { get; init; }

    /// <summary>整合包版本。</summary>
    public string? PackVersion { get; init; }

    /// <summary>游戏版本。</summary>
    public string? GameVersion { get; init; }

    /// <summary>加载器。</summary>
    public string? Loader { get; init; }

    /// <summary>加载器版本。</summary>
    public string? LoaderVersion { get; init; }

    /// <summary>目标目录。</summary>
    public string? TargetDir { get; init; }

    /// <summary>应用时间。</summary>
    public string? AppliedAt { get; init; }

    /// <summary>历史条目（最多保留 10 条）。</summary>
    public IReadOnlyList<JsonElement> History { get; init; } = [];

    /// <summary>manifest 文件路径（<c>&lt;targetDir&gt;/.mcupdater-manifest.json</c>）。</summary>
    public static string ManifestPath(string targetDir)
        => Path.Combine(targetDir, AppConfig.ManifestName);

    /// <summary>
    /// 读取安装记录。
    ///
    /// <para><b>返回 <c>null</c> 表示「当作首次运行处理」</b>，三种情况都归到这：
    /// 文件不存在、JSON 解析失败、顶层不是对象。Node 版也是统一
    /// <c>return null</c>，不做异常上抛。</para>
    ///
    /// <para><b>用严格 <c>JsonDocument</c> 而非 <c>LooseJsonParse</c></b>：
    /// manifest 是本工具自己写出的文件，不需要容忍注释和尾逗号。
    /// 放宽容错只会掩盖"文件被外部改坏"这件事。</para>
    ///
    /// <para><b>归一化照抄 Node 语义</b>：<c>mods</c> 不是数组就补空数组，
    /// <c>files</c> 不是对象就补空对象。注意 Node 是<em>就地补</em>
    /// （<c>if (!Array.isArray(j.mods)) j.mods = []</c>），
    /// 所以即使字段缺失/类型错，返回的对象里也一定有这两个键 ——
    /// 下游 <c>manifest.mods.length &gt; 0</c> 的判断依赖这一点。</para>
    /// </summary>
    public static Manifest? Load(string targetDir)
    {
        var path = ManifestPath(targetDir);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            // Node: `if (!j || typeof j !== 'object') return null`
            // JsonDocument 的根只可能是 object/array/number/string/bool/null，
            // 所以数组根也要当成无效——这与 typeof [] === 'object' 的 JS 行为**不同**，
            // 是刻意收紧：manifest 顶层按设计是对象，数组根一定是坏文件。
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // ---- mods ----
            List<ManifestMod> mods = [];
            if (root.TryGetProperty("mods", out var modsProp) && modsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in modsProp.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    mods.Add(new ManifestMod
                    {
                        Id = GetString(m, "id"),
                        FileName = GetString(m, "fileName"),
                        Version = GetString(m, "version"),
                        PlayerInstalled = GetBool(m, "playerInstalled"),
                    });
                }
            }

            // ---- files ----
            var files = new Dictionary<string, List<ManifestFile>>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("files", out var filesProp) && filesProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var cat in filesProp.EnumerateObject())
                {
                    var list = new List<ManifestFile>();
                    if (cat.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var f in cat.Value.EnumerateArray())
                        {
                            if (f.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }
                            list.Add(new ManifestFile
                            {
                                Path = GetString(f, "path"),
                                PlayerInstalled = GetBool(f, "playerInstalled"),
                            });
                        }
                    }
                    files[cat.Name] = list;
                }
            }

            // ---- 其余标量字段 ----
            List<JsonElement> history = [];
            if (root.TryGetProperty("history", out var hProp) && hProp.ValueKind == JsonValueKind.Array)
            {
                history.AddRange(hProp.EnumerateArray().Select(h => h.Clone()));
            }

            return new Manifest
            {
                Mods = mods,
                Files = files,
                PackName = GetString(root, "packName"),
                PackVersion = GetString(root, "packVersion"),
                GameVersion = GetString(root, "gameVersion"),
                Loader = GetString(root, "loader"),
                LoaderVersion = GetString(root, "loaderVersion"),
                TargetDir = GetString(root, "targetDir"),
                AppliedAt = GetString(root, "appliedAt"),
                History = history,
            };
        }
        catch (Exception)
        {
            // Node: logger.warn(`安装记录读取失败（将当作首次运行处理）: ${e.message}`)
            return null;
        }
    }

    /// <summary>
    /// 取字符串字段。<b>空串归一成 <c>null</c></b>。
    ///
    /// <para>Node 侧对 modId 有 <c>.filter(Boolean)</c>，对 packName 有
    /// <c>|| packName</c>，效果都是"空串当没有"。这里统一按空串→null 处理，
    /// 否则 <c>manifestIds</c> 里会混进空字符串，把某个解析不出 id 的 mod
    /// 误判成"整合包装的"从而默认勾选删除。</para>
    /// </summary>
    private static string? GetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var s = v.GetString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static bool? GetBool(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)
            || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }
        return v.GetBoolean();
    }
}
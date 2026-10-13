using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace McUpdater.Core;

/// <summary>单个 mod 的解析结果。</summary>
public sealed class ModInfo
{
    /// <summary>mod 标识（<c>fabric.mod.json</c> 的 id，或从文件名推断）。</summary>
    public required string Id { get; init; }

    /// <summary>展示名。没有就用 <see cref="Id"/>，文件名兜底时用文件名。</summary>
    public required string Name { get; init; }

    /// <summary>版本号（已解析 <c>${file.jarVersion}</c> 占位符）。</summary>
    public required string Version { get; init; }

    /// <summary>加载器：<c>fabric</c> / <c>quilt</c> / <c>forge</c> / <c>neoforge</c> / <c>unknown</c>。</summary>
    public required string Loader { get; init; }

    /// <summary>
    /// 是否是「猜的」—— 即前 5 级元数据都没命中，靠文件名推断。
    /// <b>这个标记必须持久化</b>，见 <see cref="DiffEngine"/> 里的说明。
    /// </summary>
    public required bool Guessed { get; init; }

    /// <summary>原始文件名（不含路径）。</summary>
    public required string FileName { get; init; }

    /// <summary>解析过程中的错误信息（不代表没有结果）。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Mod 元数据解析（移植自 Node 版 <c>src/core/mod-metadata.js</c>）。
///
/// <para>jar 本身就是 zip，需要在内存里二次解压。解析优先级严格照抄 Node：
/// <list type="number">
/// <item><c>fabric.mod.json</c> —— Fabric</item>
/// <item><c>quilt.mod.json</c> —— Quilt</item>
/// <item><c>META-INF/neoforge.mods.toml</c> —— NeoForge 1.20.5+</item>
/// <item><c>META-INF/mods.toml</c> —— Forge / 早期 NeoForge</item>
/// <item><c>mcmod.info</c> —— 1.12.2 及更早的 Forge</item>
/// <item>文件名正则 —— 兜底</item>
/// </list></para>
///
/// <para><b>为什么绝不能按文件名匹配 mod</b>：PCL2 会给 jar 改中文名
/// （<c>[JEI物品管理器] jei-xxx.jar</c>）。真实样本里 90 个 mod 有 16 个带这种前缀，
/// 全靠解析出的 <c>modId</c> 才对得上号。</para>
/// </summary>
public static class ModMetadata
{
    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    // ==================================================================
    // 容错 JSON
    // ==================================================================

    /// <summary>
    /// 一趟状态机清洗非法 JSON（照抄 Node 的 <c>sanitizeJson</c>）。
    ///
    /// <para><b>为什么必须是状态机而不是正则</b>：要精确区分「字符串内」与
    /// 「字符串外」——
    /// <list type="bullet">
    /// <item>字符串<em>内</em>的裸控制字符 → 转义（<c>\n</c> <c>\r</c> <c>\t</c>），
    ///       其余控制字符直接丢弃</item>
    /// <item>字符串<em>外</em>的 <c>//</c> 与 <c>/* */</c> → 当注释删掉</item>
    /// </list>
    /// 纯正则会在 <c>"https://example.com"</c> 上出错——
    /// 把 URL 里的 <c>//</c> 当注释起点，后半条 JSON 全被砍掉。</para>
    ///
    /// <para><b>它解决的是真实问题</b>：Node 版注释里记着，
    /// BetterGrassify 的 <c>fabric.mod.json</c> 的 description 字段里
    /// 直接嵌了裸换行，原生 <c>JSON.parse</c> 抛
    /// <c>Bad control character in string literal</c>。</para>
    /// </summary>
    public static string SanitizeJson(string src)
    {
        var sb = new StringBuilder(src.Length);
        var inStr = false;

        for (var i = 0; i < src.Length; i++)
        {
            var c = src[i];

            if (inStr)
            {
                if (c == '\\')
                {
                    // 转义序列：连下一个字符一起原样保留
                    sb.Append(c);
                    if (i + 1 < src.Length)
                    {
                        sb.Append(src[i + 1]);
                        i++;
                    }
                    continue;
                }
                if (c == '"')
                {
                    inStr = false;
                    sb.Append(c);
                    continue;
                }
                if (char.IsControl(c))
                {
                    // 字符串内的裸控制字符：能转义的转义，其余丢弃
                    if (c == '\n') sb.Append("\\n");
                    else if (c == '\r') sb.Append("\\r");
                    else if (c == '\t') sb.Append("\\t");
                    continue;
                }
                sb.Append(c);
                continue;
            }

            if (c == '"')
            {
                inStr = true;
                sb.Append(c);
                continue;
            }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n')
                {
                    i++;
                }
                sb.Append('\n');
                continue;
            }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                i += 2;
                while (i < src.Length && !(src[i] == '*' && i + 1 < src.Length && src[i + 1] == '/'))
                {
                    i++;
                }
                i++;
                continue;
            }
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 容错 JSON 解析：去 BOM → 原生解析 →（失败则）清洗 + 去尾逗号后再试。
    ///
    /// <para><b>两步策略不能省</b>：<c>System.Text.Json</c> 的严格度与
    /// V8 的 <c>JSON.parse</c> 不一致（尾逗号、注释、BOM 的处理都不同），
    /// 配置项调不出来。<b>先试原生、失败再修</b>是唯一稳妥的顺序 ——
    /// 反过来先清洗会把本来合法的 JSON 也改坏。</para>
    /// </summary>
    public static JsonDocument LooseJsonParse(string text)
    {
        // 去 BOM（U+FEFF）
        var s = text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;

        try
        {
            return JsonDocument.Parse(s, DocOptions);
        }
        catch (JsonException)
        {
            // 继续修
        }

        var cleaned = RemoveTrailingCommas(SanitizeJson(s));
        return JsonDocument.Parse(cleaned, DocOptions);
    }

    /// <summary>去尾逗号：<c>,</s*}</c> → <c>}</c>，<c>,</s*]</c> → <c>]</c>。</summary>
    private static string RemoveTrailingCommas(string s)
    {
        var sb = new StringBuilder(s.Length);
        var inStr = false;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];

            if (inStr)
            {
                if (c == '\\' && i + 1 < s.Length)
                {
                    sb.Append(c).Append(s[i + 1]);
                    i++;
                    continue;
                }
                if (c == '"')
                {
                    inStr = false;
                }
                sb.Append(c);
                continue;
            }

            if (c == '"')
            {
                inStr = true;
                sb.Append(c);
                continue;
            }

            if (c == ',')
            {
                // 往后看：跳过空白后若是 } 或 ]，说明是尾逗号，丢掉这个逗号
                var j = i + 1;
                while (j < s.Length && char.IsWhiteSpace(s[j]))
                {
                    j++;
                }
                if (j < s.Length && s[j] is '}' or ']')
                {
                    continue;
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    // ==================================================================
    // MANIFEST.MF 版本回落
    // ==================================================================

    /// <summary>
    /// 读 <c>META-INF/MANIFEST.MF</c> 里的版本，用于替换 <c>${file.jarVersion}</c>。
    /// 先试 <c>Implementation-Version</c>，再试 <c>Specification-Version</c>。
    /// </summary>
    public static string? ReadManifestVersion(ZipReader jar)
    {
        try
        {
            var entry = jar.Entry("META-INF/MANIFEST.MF");
            if (entry is null)
            {
                return null;
            }

            var txt = Encoding.UTF8.GetString(jar.Read(entry));
            foreach (var key in new[] { "Implementation-Version", "Specification-Version" })
            {
                // MANIFEST.MF 的续行以单个空格开头，先按物理行处理
                foreach (var line in txt.Split('\n'))
                {
                    var t = line.TrimEnd('\r').TrimStart();
                    if (t.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                    {
                        return t[(key.Length + 1)..].Trim();
                    }
                }
            }
        }
        catch
        {
            // 读不到就算了，交给调用方回落
        }
        return null;
    }

    /// <summary>
    /// 解析 <c>${...}</c> 占位符。
    /// 有解则用解，无解则把占位符整段删掉；删完还空则给「未知」。
    /// </summary>
    public static string ResolvePlaceholder(string? version, ZipReader jar)
    {
        if (version is null)
        {
            return string.Empty;
        }
        if (!version.Contains("${", StringComparison.Ordinal))
        {
            return version;
        }

        var fromManifest = ReadManifestVersion(jar);
        if (!string.IsNullOrEmpty(fromManifest))
        {
            return fromManifest;
        }

        var stripped = Regex.Replace(version, @"\$\{[^}]+\}", "").Trim();
        return string.IsNullOrEmpty(stripped) ? "未知" : stripped;
    }

    // ==================================================================
    // 文件名兜底
    // ==================================================================

    /// <summary>
    /// 从文件名推断 id 与 version（照抄 Node 的 <c>parseFromFileName</c>）。
    ///
    /// <para><b>顺序是刻意的，不能重排</b>：
    /// <c>[中文前缀]</c> → <c>.jar</c> → <c>.disabled</c>。
    /// 先剥 <c>.jar</c> 是因为 <c>.jar.disabled</c> 这种双后缀很常见，
    /// 而剥离用的是<em>锚定结尾</em>的正则，反了就剥不掉。</para>
    /// </summary>
    public static (string Id, string Version, bool Guessed) ParseFromFileName(string fileName)
    {
        // 先剥掉 PCL2 加的中文前缀 "[xxx] "
        var b = Regex.Replace(fileName, @"^\[[^\]]*\]\s*", "");

        // 注意顺序：.jar 在 .disabled 之前
        b = Regex.Replace(b, @"\.jar$", "", RegexOptions.IgnoreCase);
        b = Regex.Replace(b, @"\.disabled$", "", RegexOptions.IgnoreCase);

        // 常见形态：name-1.2.3+mc1.21.1 / name-fabric-1.2.3 / name_1.2.3
        var m = Regex.Match(
            b, @"^(.+?)[-_]v?(\d[\w.+\-]*)$", RegexOptions.None);

        string id, version;
        if (m.Success)
        {
            id = m.Groups[1].Value;
            version = m.Groups[2].Value;
        }
        else
        {
            id = b;
            version = "未知";
        }

        // 去掉 id 尾部的加载器标识
        id = Regex.Replace(
            id, @"[-_](fabric|forge|neoforge|quilt|mc\d[\w.]*)$", "", RegexOptions.IgnoreCase);
        id = Regex.Replace(id.ToLowerInvariant(), @"[^a-z0-9_]", "");

        // 全部字符被过滤掉时回落用原始 base（Node: id || base.toLowerCase()）
        if (id.Length == 0)
        {
            id = b.ToLowerInvariant();
        }

        return (id, version, true);
    }

    // ==================================================================
    // 主入口
    // ==================================================================

    /// <summary>
    /// 解析一个 jar 的元数据。<paramref name="jarBuf"/> 是 jar 完整内容
    /// （jar 本身是 zip，在内存里二次解压）。
    /// </summary>
    public static ModInfo ParseJar(byte[] jarBuf, string fileName)
    {
        ZipReader? jar = null;
        try
        {
            jar = ZipReader.FromBuffer(jarBuf);
            _ = jar.Entries;
        }
        catch (Exception ex)
        {
            var fb = ParseFromFileName(fileName);
            return new ModInfo
            {
                Id = fb.Id,
                Name = fileName,
                Version = fb.Version,
                Loader = "unknown",
                Guessed = true,
                FileName = fileName,
                Error = $"jar 无法打开: {ex.Message}",
            };
        }

        using (jar)
        {
            try
            {
                // ---- 1. Fabric ----
                if (jar.Has("fabric.mod.json"))
                {
                    using var d = LooseJsonParse(jar.ReadText("fabric.mod.json"));
                    if (d.RootElement.ValueKind == JsonValueKind.Object
                        && d.RootElement.TryGetProperty("id", out var idEl)
                        && idEl.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    {
                        var id = idEl.ToString();
                        var name = GetString(d.RootElement, "name") ?? id;
                        return new ModInfo
                        {
                            Id = id,
                            Name = name,
                            Version = ResolvePlaceholder(GetRawString(d.RootElement, "version"), jar),
                            Loader = "fabric",
                            Guessed = false,
                            FileName = fileName,
                        };
                    }
                }

                // ---- 2. Quilt ----
                if (jar.Has("quilt.mod.json"))
                {
                    using var d = LooseJsonParse(jar.ReadText("quilt.mod.json"));
                    if (d.RootElement.ValueKind == JsonValueKind.Object
                        && d.RootElement.TryGetProperty("quilt_loader", out var ql)
                        && ql.ValueKind == JsonValueKind.Object
                        && ql.TryGetProperty("id", out var qid)
                        && qid.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    {
                        var id = qid.ToString();
                        string? name = null;
                        if (ql.TryGetProperty("metadata", out var meta)
                            && meta.ValueKind == JsonValueKind.Object)
                        {
                            name = GetString(meta, "name");
                        }
                        return new ModInfo
                        {
                            Id = id,
                            Name = name ?? id,
                            Version = ResolvePlaceholder(GetRawString(ql, "version"), jar),
                            Loader = "quilt",
                            Guessed = false,
                            FileName = fileName,
                        };
                    }
                }

                // ---- 3/4. NeoForge / Forge（TOML 解析在 TomlLite 里）----
                foreach (var (path, loader) in new[]
                {
                    ("META-INF/neoforge.mods.toml", "neoforge"),
                    ("META-INF/mods.toml", "forge"),
                })
                {
                    if (!jar.Has(path))
                    {
                        continue;
                    }
                    var t = TomlLite.Parse(jar.ReadText(path));
                    if (t.Mods.Count > 0 && t.Mods[0].ModId is { Length: > 0 } mid)
                    {
                        var display = t.Mods[0].DisplayName ?? mid;
                        return new ModInfo
                        {
                            Id = mid,
                            Name = display,
                            Version = ResolvePlaceholder(t.Mods[0].Version, jar),
                            Loader = loader,
                            Guessed = false,
                            FileName = fileName,
                        };
                    }
                }

                // ---- 5. 老版 Forge mcmod.info ----
                if (jar.Has("mcmod.info"))
                {
                    using var d = LooseJsonParse(jar.ReadText("mcmod.info"));
                    JsonElement? first = default;
                    var have = false;

                    if (d.RootElement.ValueKind == JsonValueKind.Array
                        && d.RootElement.GetArrayLength() > 0)
                    {
                        first = d.RootElement[0];
                        have = true;
                    }
                    else if (d.RootElement.ValueKind == JsonValueKind.Object
                             && d.RootElement.TryGetProperty("modList", out var ml)
                             && ml.ValueKind == JsonValueKind.Array
                             && ml.GetArrayLength() > 0)
                    {
                        first = ml[0];
                        have = true;
                    }

                    if (have && first!.Value.ValueKind == JsonValueKind.Object)
                    {
                        var id = GetString(first.Value, "modid");
                        if (!string.IsNullOrEmpty(id))
                        {
                            return new ModInfo
                            {
                                Id = id!,
                                Name = GetString(first.Value, "name") ?? id!,
                                // 注意：mcmod.info 这一级**不做占位符解析**
                                // （Node 版同样如此，照搬不「修好」）
                                Version = GetString(first.Value, "version") ?? "未知",
                                Loader = "forge",
                                Guessed = false,
                                FileName = fileName,
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 解析失败，落到文件名兜底
                var fb = ParseFromFileName(fileName);
                return new ModInfo
                {
                    Id = fb.Id,
                    Name = fileName,
                    Version = fb.Version,
                    Loader = "unknown",
                    Guessed = true,
                    FileName = fileName,
                    Error = ex.Message,
                };
            }
        }

        // ---- 6. 兜底 ----
        var fallback = ParseFromFileName(fileName);
        return new ModInfo
        {
            Id = fallback.Id,
            Name = Regex.Replace(
                fileName, @"\.jar$", "", RegexOptions.IgnoreCase),
            Version = fallback.Version,
            Loader = "unknown",
            Guessed = true,
            FileName = fileName,
        };
    }

    private static string? GetString(JsonElement obj, string prop)
    {
        var raw = GetRawString(obj, prop);
        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    private static string? GetRawString(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var el) && el.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? el.ToString()
            : null;
}
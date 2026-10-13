using System.Globalization;

namespace McUpdater.Core;

/// <summary>mods.toml 里的一条 <c>[[mods]]</c> 记录（只保留 diff 链路要用的三个字段）。</summary>
public sealed class TomlMod
{
    public string? ModId { get; set; }
    public string? DisplayName { get; set; }
    public string? Version { get; set; }
}

/// <summary>TOML 解析结果。</summary>
public sealed class TomlDoc
{
    /// <summary><c>[[mods]]</c> 下的全部记录，按文件出现顺序。</summary>
    public List<TomlMod> Mods { get; init; } = [];
}

/// <summary>
/// 极简 TOML 子集解析（移植自 Node 版 <c>src/core/toml-lite.js</c>）。
///
/// <para><b>刻意只实现本项目要用的部分</b>：Node 版是个通用小引擎，
/// 这里缩到「能读出 <c>mods.toml</c> 的 <c>[[mods]]</c> 三字段」。
/// 支持 table / array-of-tables / 基本类型 / 注释 / 三种引号；
/// 不支持日期时间、内联表、数组嵌套、点分键 ——
/// <c>mods.toml</c> 用不到，而每多支持一样就多一处可能与Node 不一致的地方。</para>
/// </summary>
public static class TomlLite
{
    /// <summary>
    /// 解析 mods.toml。<paramref name="text"/> 会被就地容错：
    /// 任何看不懂的行<em>跳过</em>而不是抛异常 —— 一个 mod 的元数据解析失败
    /// 不该让整个 diff 流程崩掉。
    /// </summary>
    public static TomlDoc Parse(string? text)
    {
        var doc = new TomlDoc();
        if (string.IsNullOrEmpty(text))
        {
            return doc;
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');

        // currentTable 指向当前 [[mods]] 表（null = 根或其他 table）
        TomlMod? currentMod = null;
        var inModsArray = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            // ---- [[array of tables]] ----
            if (trimmed.StartsWith("[[", StringComparison.Ordinal))
            {
                var close = trimmed.IndexOf("]]", 2, StringComparison.Ordinal);
                if (close > 0)
                {
                    var key = Unquote(trimmed[2..close].Trim());
                    inModsArray = key == "mods";
                    if (inModsArray)
                    {
                        currentMod = new TomlMod();
                        doc.Mods.Add(currentMod);
                    }
                    else
                    {
                        currentMod = null;
                    }
                    continue;
                }
            }

            // ---- [table] ----
            if (trimmed.StartsWith('['))
            {
                var close = trimmed.IndexOf(']');
                if (close > 0)
                {
                    // 进入别的 table 就退出 mods 上下文
                    inModsArray = false;
                    currentMod = null;
                }
                continue;
            }

            // ---- key = value ----
            var eq = trimmed.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var k = Unquote(trimmed[..eq].Trim());

            // 多行字符串：""" 或 '''
            if (trimmed[(eq + 1)..].TrimStart() is var v0 && v0.Length >= 3
                && (v0.StartsWith("\"\"\"", StringComparison.Ordinal)
                    || v0.StartsWith("'''", StringComparison.Ordinal)))
            {
                var delim = v0[..3];
                var rest = v0[3..];
                string body;
                if (rest.TrimEnd().EndsWith(delim, StringComparison.Ordinal)
                    && rest.TrimEnd().Length >= 3)
                {
                    body = rest.TrimEnd()[..^3];
                }
                else
                {
                    var parts = new List<string> { rest };
                    while (i + 1 < lines.Length)
                    {
                        i++;
                        var l = lines[i];
                        var idx = l.IndexOf(delim, StringComparison.Ordinal);
                        if (idx >= 0)
                        {
                            parts.Add(l[..idx]);
                            break;
                        }
                        parts.Add(l);
                    }
                    body = string.Join('\n', parts);
                }

                // TOML 规定紧跟定界符的首个换行要被剔除
                if (body.StartsWith('\n'))
                {
                    body = body[1..];
                }
                Assign(currentMod, inModsArray, k, body);
                continue;
            }

            Assign(currentMod, inModsArray, k,
                ParseValue(StripInlineComment(trimmed[(eq + 1)..].Trim())));
        }

        return doc;
    }

    private static void Assign(TomlMod? currentMod, bool inModsArray, string key, object? value)
    {
        if (!inModsArray || currentMod is null)
        {
            return;
        }
        switch (key)
        {
            case "modId":
                currentMod.ModId = AsString(value);
                break;
            case "displayName":
                currentMod.DisplayName = AsString(value);
                break;
            case "version":
                currentMod.Version = AsString(value);
                break;
        }
    }

    private static string? AsString(object? v) => v switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        double d => d.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    /// <summary>
    /// 去掉行尾注释，但不动字符串里的 <c>#</c>。
    /// <c>description = "某个 # 号"</c> 必须原样保留。
    /// </summary>
    private static string StripInlineComment(string s)
    {
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];

            if (c == '\\' && inDouble)
            {
                i++;      // 跳过被转义的字符
                continue;
            }
            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if (c == '#' && !inSingle && !inDouble)
            {
                return s[..i];
            }
        }
        return s;
    }

    private static object? ParseValue(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0)
        {
            return null;
        }
        if (s == "true")
        {
            return true;
        }
        if (s == "false")
        {
            return false;
        }

        // 整数 / 浮点
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i0))
        {
            return i0;
        }
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f0))
        {
            return f0;
        }

        // 双引号（含转义）
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            return Unescape(s[1..^1]);
        }
        // 单引号（字面量，不解转义）
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'')
        {
            return s[1..^1];
        }

        // 单行数组：只当作原始字符串存，diff 链路用不到数组内容
        if (s.StartsWith('['))
        {
            return s;
        }

        return s;
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\'))
        {
            return s;
        }

        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length)
            {
                sb.Append(s[i]);
                continue;
            }

            var n = s[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case '"': sb.Append('"'); break;
                case '\'': sb.Append('\''); break;
                case '\\': sb.Append('\\'); break;
                case 'u' when i + 4 < s.Length
                             && int.TryParse(s.AsSpan(i + 1, 4),
                                 NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp):
                    sb.Append((char)cp);
                    i += 4;
                    break;
                default: sb.Append(n); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>剥掉键名或表名两端可能存在的引号。</summary>
    private static string Unquote(string s)
    {
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0])
        {
            return s[1..^1];
        }
        return s;
    }
}
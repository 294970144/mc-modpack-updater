using System.Text;
using System.Text.RegularExpressions;

namespace McUpdater.Core;

/// <summary>
/// 文件系统辅助（移植自 Node 版 <c>src/utils/fs-helpers.js</c>）。
///
/// <para><b>只移植 diff 链路用得到的部分</b> —— Node 版共 14 个导出，
/// 这里只搬 4 个：<c>crc32</c> / <c>listFilesRecursive</c> /
/// <c>makeMatcher</c>（含 <c>globToRegExp</c>）/ <c>hasTraversal</c>。
/// 其余（<c>copyDirSync</c> / <c>rmrf</c> / <c>dirStats</c> /
/// <c>isSafeChild</c> / <c>timestampName</c> / <c>fileFingerprint</c>）
/// 都是 applier（应用变更）专用，不在本次范围内。</para>
///
/// <para><b>那个刻意"不优化"的 glob 零层语义</b>：<c>**/</c> 会被翻译成
/// <c>(?:.*/)?</c> —— 那个 <c>?</c> 不能省。省掉它 <c>**/*.zip</c> 就只匹配
/// 子目录里的文件、匹配不到根目录的 <c>config/xxx.zip</c>，
/// 于是玩家的配置备份文件会被当成"待删除项"列进变更清单。
/// 这属于 Node 版标注的 P0-2 同一类（安全），看起来像啰嗦写法，其实是承重墙。</para>
/// </summary>
public static class FsHelpers
{
    // ------------------------------------------------------------------
    // CRC32
    // ------------------------------------------------------------------

    /// <summary>CRC32 查找表（多项式 0xEDB88320），延迟构建。</summary>
    private static uint[]? _crcTable;

    /// <summary>
    /// 计算 CRC32。
    ///
    /// <para><b>手写表而不用 System.IO.Hashing.Crc32</b>：后者需要显式
    /// PackageReference，且 Node 版本身就有手写表实现（作为 zlib.crc32
    /// 缺失时的兜底）。手写保持零依赖，测试台与主程序都不必为它加包。</para>
    ///
    /// <para><b>返回值必须是无符号的</b>：Node 侧写的是 <c>return (crc ^ -1) >>> 0</c>。
    /// 若这里用 <c>int</c> 收尾，负数会让 fingerprint 变成
    /// <c>"12345:-1234567"</c>，与 zip 中央目录里存的无符号值对不上，
    /// 于是**所有 unchanged 会被误判成 updated** —— 这是会出真错的 bug，
    /// 不是性能退化。</para>
    /// </summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        _crcTable ??= BuildCrcTable();

        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = (crc >> 8) ^ _crcTable[(crc ^ b) & 0xFF];
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    /// <summary>读文件并算CRC32。读不到返回 -1（调用方据此判定"取不到指纹"）。</summary>
    public static uint Crc32OfFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return Crc32(bytes);
        }
        catch
        {
            return unchecked((uint)-1);
        }
    }

    // ------------------------------------------------------------------
    // 路径遍历
    // ------------------------------------------------------------------

    /// <summary>
    /// 是否含路径穿越（Node: <c>hasTraversal</c>）。
    ///
    /// <para>Node 版是 <c>p.includes('..')</c> —— 只要字符串里任何位置出现
    /// <c>..</c> 就判定为穿越，不管它是不是路径的一段。这是**刻意过严**的：
    /// 宁可误伤 <c>foo..bar.zip</c>，也不能放过 <c>../../evil</c>。
    /// 移植时保持这个语义，不要"优化"成逐段检查。</para>
    /// </summary>
    public static bool HasTraversal(string? path)
        => path is null || path.Contains("..", StringComparison.Ordinal);

    // ------------------------------------------------------------------
    // glob
    // ------------------------------------------------------------------

    /// <summary>
    /// 极简 glob → 正则（Node: <c>globToRegExp</c>）。只支持 <c>**</c>、<c>*</c>、<c>?</c>。
    ///
    /// <para><b>与 Node 版逐字符对齐</b>，包括那个容易漏掉的点：
    /// <c>**/</c> 要能匹配<em>零层</em>目录，所以翻译成 <c>(?:.*/)?</c>
    /// 而不是 <c>.* /</c>。</para>
    /// </summary>
    public static Regex GlobToRegex(string pattern)
    {
        var sb = new StringBuilder("^");

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    i++;
                    // `**/` 吃掉后面的斜杠，用 (?:.*/)? 表示"零层或多层目录"
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                // Node 里的正则： /[.+^${}()|[\]\\]/g
                if (c is '.' or '+' or '^' or '$' or '{' or '}' or '(' or ')' or '|' or '[' or ']' or '\\')
                {
                    sb.Append('\\');
                }
                sb.Append(c);
            }
        }

        sb.Append('$');

        // CultureInvariant：Node 的 'i' 标志没有土耳其语 I 问题，
        // 而 .NET 的 IgnoreCase 在部分 culture 下对 'I'/'i' 的判定是 culture 相关的。
        // 加上 CultureInvariant 才能真正对齐 Node 行为。
        return new Regex(sb.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// 把一组 glob 编译成一个匹配函数（Node: <c>makeMatcher</c>）。
    /// 空集合返回恒 false 的谓词 —— 这正是 <c>mods</c> 分类的情况
    /// （<c>CATEGORY_PROTECTED</c> 里 mods 没有条目）。
    /// </summary>
    public static Func<string, bool> MakeMatcher(IReadOnlyList<string> patterns)
    {
        if (patterns is null || patterns.Count == 0)
        {
            return static _ => false;
        }

        var regexes = patterns.Select(GlobToRegex).ToArray();
        return path =>
        {
            // 路径统一用 / 分隔后再匹配（Node: String(p).replace(/\\/g, '/')）
            var s = path.Replace('\\', '/');
            return regexes.Any(r => r.IsMatch(s));
        };
    }

    // ------------------------------------------------------------------
    // 目录遍历
    // ------------------------------------------------------------------

    /// <summary>
    /// 递归列出目录下所有文件（相对路径，统一 <c>/</c> 分隔）。
    /// 目录不存在时返回空列表，不抛异常。
    /// </summary>
    public static List<string> ListFilesRecursive(string root, string relBase = "")
    {
        var result = new List<string>();
        var current = string.IsNullOrEmpty(relBase) ? root : Path.Combine(root, relBase);

        if (!Directory.Exists(current))
        {
            return result;
        }

        foreach (var path in Directory.EnumerateFiles(current))
        {
            var name = Path.GetFileName(path);
            result.Add(string.IsNullOrEmpty(relBase) ? name : $"{relBase}/{name}");
        }

        foreach (var path in Directory.EnumerateDirectories(current))
        {
            var name = Path.GetFileName(path);
            var rel = string.IsNullOrEmpty(relBase) ? name : $"{relBase}/{name}";
            result.AddRange(ListFilesRecursive(root, rel));
        }

        return result;
    }
}

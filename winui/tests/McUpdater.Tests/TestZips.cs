using System.IO.Compression;
using System.Text;

namespace McUpdater.Core.Tests;

/// <summary>
/// 测试用 ZIP 构造器。
///
/// <para>用 <see cref="System.IO.Compression.ZipArchive"/> 而不是手写字节 ——
/// ModpackReader 的测试关注的是"能不能识别 mrpack / 内容根 / 分类统计"，
/// 而不是 ZIP 格式本身的解析（后者已由 ZipReaderTests 覆盖）。</para>
/// </summary>
internal static class TestZips
{
    /// <summary>构造一个扁平 zip（路径 + 内容，null 表示目录条目）。</summary>
    public static byte[] MakeZip(params (string Path, string? Content)[] items)
        => MakeZipRaw(items.Select(i => (i.Path, i.Content is null ? null : Encoding.UTF8.GetBytes(i.Content)))
            .ToArray());

    /// <summary>构造 zip，内容为原始字节。</summary>
    public static byte[] MakeZipRaw(params (string Path, byte[]? Content)[] items)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in items)
            {
                var name = path.EndsWith('/') ? path[..^1] : path;
                var entry = zip.CreateEntry(name);
                if (content is null)
                {
                    continue;
                }
                using var s = entry.Open();
                s.Write(content, 0, content.Length);
            }
        }
        return ms.ToArray();
    }

    /// <summary>
    /// 构造 PCL2 式外层 zip：内含一个 <c>modpack.mrpack</c>。
    /// </summary>
    /// <param name="innerItems">内层 mrpack 的条目。</param>
    /// <param name="indexJson"><c>modrinth.index.json</c> 内容，null 表示不放该文件。</param>
    /// <param name="mrpackContentRoot">
    /// 为 <c>""</c> 时内层条目不加<code>overrides/</code> 前缀（测试空内容根场景）。
    /// </param>
    public static byte[] BuildMrpack(
        (string Path, string? Content)[] innerItems,
        string? indexJson = null,
        string mrpackContentRoot = "overrides")
    {
        var all = new List<(string, string?)>();
        if (indexJson is not null)
        {
            all.Add(("modrinth.index.json", indexJson));
        }
        foreach (var (p, c) in innerItems)
        {
            all.Add((mrpackContentRoot.Length == 0 ? p : $"{mrpackContentRoot}/{p}", c));
        }
        return MakeZip(all.ToArray());
    }

    /// <summary>
    /// 构造 <b>PCL2 外层 zip</b>：内含一个 <c>modpack.mrpack</c> 条目。
    /// 这才是 <c>ModpackReader.Open</c> 快速路径真正吃的形状。
    /// </summary>
    /// <param name="compressInner">
    /// true 时内层 mrpack 条目用 Deflate（method=8，走"进内存"分支）；
    /// false 时用 STORED（method=0，走子区间直读分支）。
    /// </param>
    public static byte[] BuildPcl2Outer(
        (string Path, string? Content)[] innerItems,
        bool compressInner,
        string? indexJson = null,
        string mrpackContentRoot = "overrides")
    {
        var inner = BuildMrpack(innerItems, indexJson, mrpackContentRoot);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("modpack.mrpack", compressInner
                ? CompressionLevel.Optimal
                : CompressionLevel.NoCompression);
            using var s = entry.Open();
            s.Write(inner, 0, inner.Length);
        }
        return ms.ToArray();
    }

    /// <summary>把一份 mrpack 字节包进 PCL2 外层 zip（STORED，走子区间直读分支）。</summary>
    public static byte[] WrapMrpack(byte[] innerMrpack)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("modpack.mrpack", CompressionLevel.NoCompression);
            using var s = entry.Open();
            s.Write(innerMrpack, 0, innerMrpack.Length);
        }
        return ms.ToArray();
    }
}
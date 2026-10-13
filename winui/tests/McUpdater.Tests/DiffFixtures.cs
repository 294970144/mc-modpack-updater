using System.IO.Compression;
using System.Text;

namespace McUpdater.Core.Tests;

/// <summary>
/// 供 diff 测试使用的整合包/本地目录构造器。
///
/// <para><b>jar 的字节必须可复现</b>：diff 的第一阶段是「指纹预筛」，
/// (size + crc32) 相同即判unchanged。若 jar 的字节随时间戳变化，
/// 测试就会随机在unchanged / updated 之间跳。所以这里手写 STORED zip
/// 并固定 DOS 时间字段，保证同一份元数据永远产出同一串字节。</para>
///
/// <para>手写 zip 而不用 <see cref="ZipArchive"/>：后者会写入当前时间，
/// 让「字节完全一致」这个前提无法成立。</para>
/// </summary>
internal static class DiffFixtures
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] buf)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in buf)
        {
            crc = (crc >> 8) ^ CrcTable[(crc ^ b) & 0xFF];
        }
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>构造只含 STORED 条目的 zip。</summary>
    public static byte[] StoredZip(params (string Name, byte[] Data)[] entries)
    {
        var locals = new List<byte[]>();
        var centrals = new List<byte[]>();
        long offset = 0;

        foreach (var (name, data) in entries)
        {
            var nameBuf = Encoding.UTF8.GetBytes(name);
            var crc = Crc32(data);

            var lh = new byte[30];
            WriteU32(lh, 0, 0x04034b50);
            WriteU16(lh, 4, 20);        // version needed
            WriteU16(lh, 6, 0x0800);    // UTF-8 标志位
            WriteU16(lh, 8, 0);         // method = STORED
            WriteU16(lh, 10, 0);        // time
            WriteU16(lh, 12, 0x21);     // date 固定 → 字节可复现
            WriteU32(lh, 14, crc);
            WriteU32(lh, 18, (uint)data.Length);
            WriteU32(lh, 22, (uint)data.Length);
            WriteU16(lh, 26, (ushort)nameBuf.Length);
            WriteU16(lh, 28, 0);
            locals.Add(lh);
            locals.Add(nameBuf);
            locals.Add(data);

            var ch = new byte[46];
            WriteU32(ch, 0, 0x02014b50);
            WriteU16(ch, 4, 20);
            WriteU16(ch, 6, 20);
            WriteU16(ch, 8, 0x0800);
            WriteU16(ch, 10, 0);
            WriteU16(ch, 12, 0);
            WriteU16(ch, 14, 0x21);
            WriteU32(ch, 16, crc);
            WriteU32(ch, 20, (uint)data.Length);
            WriteU32(ch, 24, (uint)data.Length);
            WriteU16(ch, 28, (ushort)nameBuf.Length);
            WriteU16(ch, 30, 0);
            WriteU16(ch, 32, 0);
            WriteU16(ch, 34, 0);
            WriteU16(ch, 36, 0);
            WriteU32(ch, 38, 0);
            WriteU32(ch, 42, (uint)offset);
            centrals.Add(ch);
            centrals.Add(nameBuf);

            offset += 30 + nameBuf.Length + data.Length;
        }

        var cd = centrals.SelectMany(x => x).ToArray();
        var eocd = new byte[22];
        WriteU32(eocd, 0, 0x06054b50);
        WriteU16(eocd, 8, (ushort)entries.Length);
        WriteU16(eocd, 10, (ushort)entries.Length);
        WriteU32(eocd, 12, (uint)cd.Length);
        WriteU32(eocd, 16, (uint)offset);

        return [.. locals.SelectMany(x => x), .. cd, .. eocd];
    }

    private static void WriteU16(byte[] b, int o, ushort v)
    {
        b[o] = (byte)(v & 0xFF);
        b[o + 1] = (byte)(v >> 8);
    }

    private static void WriteU32(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v & 0xFF);
        b[o + 1] = (byte)((v >> 8) & 0xFF);
        b[o + 2] = (byte)((v >> 16) & 0xFF);
        b[o + 3] = (byte)((v >> 24) & 0xFF);
    }

    /// <summary>造一个只含 <c>fabric.mod.json</c> 的最小 jar。</summary>
    public static byte[] Jar(string id, string name, string version)
    {
        var meta = Encoding.UTF8.GetBytes(
            $$"""{"schemaVersion":1,"id":"{{id}}","version":"{{version}}","name":"{{name}}"}""");
        return StoredZip(("fabric.mod.json", meta));
    }

    /// <summary>
    /// 造整合包（mrpack 本体：有 modrinth.index.json + overrides/）。
    /// </summary>
    /// <summary>
    /// 造整合包（mrpack 本体：有 modrinth.index.json + overrides/）。
    /// </summary>
    /// <param name="mods">
    /// <b>文件名</b> → jar 字节。会自动挂到 <c>overrides/mods/</c> 下，
    /// 这里只写 <c>a.jar</c> 而非 <c>overrides/mods/a.jar</c>。
    /// </param>
    /// <param name="indexJson">index 内容，null 用默认。</param>
    /// <param name="extra">
    /// 额外的条目，<b>路径是完整 zip 内路径</b>（如 <c>overrides/config/a.toml</c>），
    /// 不做前缀拼接 —— 故意与 <paramref name="mods"/> 的行为不同，
    /// 这样测试才能构造出 mods 目录下的非 jar 文件（readme.txt 之类）。
    /// </param>
    public static byte[] Mrpack(
        Dictionary<string, byte[]> mods,
        string? indexJson = null,
        (string Path, byte[] Data)[]? extra = null)
    {
        var entries = new List<(string, byte[])>
        {
            ("modrinth.index.json", Encoding.UTF8.GetBytes(indexJson ?? DefaultIndex)),
        };
        foreach (var (f, buf) in mods)
        {
            entries.Add(($"overrides/mods/{f}", buf));
        }
        if (extra is not null)
        {
            entries.AddRange(extra);
        }
        return StoredZip([.. entries]);
    }

    public const string DefaultIndex = """
        {"name":"基准包","versionId":"1.0.0",
         "dependencies":{"minecraft":"1.21.11","fabric-loader":"0.16.10"},
         "files":[]}
        """;

    /// <summary>把整合包字节落盘（<see cref="ModpackReader.Open"/> 需要真实文件）。</summary>
    public static string WritePack(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcdiff-{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>建一个临时本地目录，返回其路径（调用方负责删除）。</summary>
    public static string MakeLocalDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mcdiff-local-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void DeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // 测试清理失败不应掩盖真正的断言结果
        }
    }
}
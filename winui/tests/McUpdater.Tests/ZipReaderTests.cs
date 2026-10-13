using System.IO.Compression;
using System.Text;
using McUpdater.Core;
using Xunit;

namespace McUpdater.Tests;

/// <summary>
/// <see cref="ZipReader"/> 的合成用例 —— 专打真实样本覆盖不到的分支。
///
/// <para><b>为什么需要合成用例</b>：真实样本 <c>1.21.11 1.0.6.zip</c> 里
/// 19 个含中文的条目<em>全部标了 UTF-8 标志位</em>，一条都没落到 GBK 回退上。
/// 也就是说「文件名解码」这条路径上最脆弱的一半（GBK 回退）根本没被真实数据验证过 ——
/// 而国内老整合包、PCL2 早期产物恰恰是不标标志位的。</para>
/// </summary>
public sealed class ZipReaderTests
{
    /// <summary>
    /// GBK provider 注册 —— xunit 每个测试类都会new 一个实例，
    /// 放构造函数里等价于「每个用例前都注册一次」，幂等无副作用。
    /// </summary>
    public ZipReaderTests() => ZipReader.EnsureGbk();
    // ==================================================================
    // 文件名解码：GBK 回退
    //
    // 【为什么必须手工拼字节】
    // System.IO.Compression.ZipArchive.CreateEntry 内部按名字内容
    // 自动决定是否设置 UTF-8 标志位（bit 11），**没有开关**。
    // 而「中文名 + 不标标志位」正是国内老整合包 / PCL2 早期产物的真实形态 ——
    // 也正是 ZipReader.DecodeName 里 GBK 回退分支唯一存在的理由。
    // 所以这里不用 ZipArchive，直接按 APPNOTE 规范手写一个最小 zip：
    // STORED（method=0），无 data descriptor，单条目。
    // 字节是照着规范数出来的，不是猜的。
    // ==================================================================

    [Fact]
    public void 未标UTF8标志位的GBK中文名应正确回退解码()
    {
        ZipReader.EnsureGbk();   // 构造 GBK 字节前必须注册 provider

        const string name = "中文名称.jar";
        var bytes = BuildMinimalZip(
            nameBytes: Encoding.GetEncoding(936).GetBytes(name),
            utf8Flag: false,
            content: [1, 2, 3, 4]);

        using var zip = ZipReader.FromBuffer(bytes);

        var entry = zip.Entries[0];
        Assert.Equal(name, entry.Name);   // 若回退失效，这里会是乱码
        Assert.Equal(4, entry.Size);
        Assert.Equal([1, 2, 3, 4], zip.Read(entry));
    }

    [Fact]
    public void 未标UTF8标志位的ASCII名应直接可读()
    {
        const string name = "config/animatica.properties";
        var bytes = BuildMinimalZip(
            nameBytes: Encoding.ASCII.GetBytes(name),
            utf8Flag: false,
            content: [9, 9]);

        using var zip = ZipReader.FromBuffer(bytes);

        Assert.Equal(name, zip.Entries[0].Name);
        Assert.True(zip.Has(name));
    }

    [Fact]
    public void 同一份GBK字节_标志位不同应解出同一名字()
    {
        const string name = "模组配置.jar";
        var gbk = Encoding.GetEncoding(936).GetBytes(name);
        var utf8 = Encoding.UTF8.GetBytes(name);

        using var asGbk = ZipReader.FromBuffer(BuildMinimalZip(gbk, utf8Flag: false, content: [0]));
        using var asUtf8 = ZipReader.FromBuffer(BuildMinimalZip(utf8, utf8Flag: true, content: [0]));

        // 两条分支必须殊途同归 —— 这是 DecodeName 三级回退的语义
        Assert.Equal(asGbk.Entries[0].Name, asUtf8.Entries[0].Name);
        Assert.Equal(name, asUtf8.Entries[0].Name);
    }

    [Fact]
    public void UTF8标志位下四字节emoji应正确解码()
    {
        // 真实样本里的 [玉 🔍]，放大镜是 4 字节 UTF-8（f0 9f 94 8d）
        const string name = "[玉 🔍] Jade.jar";
        var bytes = BuildMinimalZip(Encoding.UTF8.GetBytes(name), utf8Flag: true, content: [7]);

        using var zip = ZipReader.FromBuffer(bytes);
        Assert.Equal(name, zip.Entries[0].Name);
    }

    /// <summary>
    /// 按 APPNOTE 规范手写一个最小 zip（STORED，单条目，无 data descriptor）。
    ///
    /// <para><b>各段布局</b>：
    /// <code>
    /// [0]  Local File Header  30 字节 + nameLen
    ///  [...] 数据
    ///  [...] Central Directory Header  46 字节 + nameLen
    ///  [...] End of Central Directory  22 字节
    /// </code></para>
    /// </summary>
    private static byte[] BuildMinimalZip(byte[] nameBytes, bool utf8Flag, byte[] content)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        var crc = FsHelpers.Crc32(content);
        var flags = utf8Flag ? 0x0800 : 0;

        void U16(int v) => w.Write((ushort)v);
        void U32(uint v) => w.Write(v);

        // ---- Local File Header（固定 30 字节）----
        w.Write(0x04034B50u);       //  0 signature
        U16(20);                    //  4 version needed
        U16(flags);                 //  6 flags ← UTF-8 标志位（bit 11）在这里
        U16(0);                     //  8 method = STORED
        U16(0); U16(0x0021);        // 10 time / date（固定值，无所谓）
        U32(crc);                   // 14 crc32
        U32((uint)content.Length);   // 18 compressed size
        U32((uint)content.Length);   // 22 uncompressed size
        U16(nameBytes.Length);      // 26 name len ← ZipReader.DataOffset 要读它
        U16(0);                     // 28 extra len ← ZipReader.DataOffset 要读它
        w.Write(nameBytes);
        w.Write(content);

        var cdOffset = (uint)ms.Position;

        // ---- Central Directory Header（固定 46 字节）----
        w.Write(0x02014B50u);       //  0
        U16(20); U16(20);           //  4 version made by / needed
        U16(flags);                 //  8
        U16(0);                     // 10 method
        U16(0); U16(0x0021);        // 12 time / date
        U32(crc);                   // 16
        U32((uint)content.Length);   // 20 compressed size
        U32((uint)content.Length);   // 24 uncompressed size
        U16(nameBytes.Length);      // 28
        U16(0);                     // 30 extra len
        U16(0);                     // 32 comment len
        U16(0);                     // 34 disk number start
        U16(0);                     // 36 internal attrs
        U32(0);                     // 38 external attrs
        U32(0);                     // 42 local header offset ← 本地头就在文件开头，故为 0
        w.Write(nameBytes);

        var cdSize = (uint)ms.Position - cdOffset;

        // ---- End of Central Directory ----
        w.Write(0x06054B50u);
        U16(0); U16(0);
        U16(1); U16(1);             // 本盘/总盘 条目数
        U32(cdSize);
        U32(cdOffset);
        U16(0);                     // comment length

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// 造一个带<em>两条同名条目</em>的 zip —— 现实中会出现在手工重打包的包上。
    /// <b>用于验证 <see cref="ZipReader"/> 与 Node 的 Map 同语义：后者覆盖前者。</b>
    /// </summary>
    private static byte[] BuildZipWithDuplicateNames(string name, byte[] first, byte[] second)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        void U16(int v) => w.Write((ushort)v);
        void U32(uint v) => w.Write(v);

        var offsets = new List<uint>();
        foreach (var content in new[] { first, second })
        {
            var crc = FsHelpers.Crc32(content);
            offsets.Add((uint)ms.Position);

            w.Write(0x04034B50u);
            U16(20); U16(0x0800); U16(0);
            U16(0); U16(0x0021);
            U32(crc);
            U32((uint)content.Length);
            U32((uint)content.Length);
            U16(nameBytes.Length); U16(0);
            w.Write(nameBytes);
            w.Write(content);
        }

        var cdOffset = (uint)ms.Position;
        var crcFirst = FsHelpers.Crc32(first);
        var crcSecond = FsHelpers.Crc32(second);

        // 两条中央目录记录，顺序与本地头一致
        WriteCd(crcFirst, first.Length, offsets[0]);
        WriteCd(crcSecond, second.Length, offsets[1]);

        var cdSize = (uint)ms.Position - cdOffset;
        w.Write(0x06054B50u);
        U16(0); U16(0); U16(2); U16(2);
        U32(cdSize); U32(cdOffset); U16(0);

        w.Flush();
        return ms.ToArray();

        void WriteCd(uint crc, int len, uint localOffset)
        {
            // 固定 46 字节，逐字段对齐 APPNOTE（顺序错一个字节就整体报废）
            w.Write(0x02014B50u);   //  0 signature
            U16(20);                 //  4 version made by
            U16(20);                 //  6 version needed
            U16(0x0800);             //  8 flags（UTF-8）
            U16(0);                  // 10 method = STORED
            U16(0); U16(0x0021);     // 12 time / date
            U32(crc);                // 16 crc32
            U32((uint)len);          // 20 compressed size
            U32((uint)len);          // 24 uncompressed size
            U16(nameBytes.Length);   // 28 name len
            U16(0);                  // 30 extra len
            U16(0);                  // 32 comment len
            U16(0);                  // 34 disk number start
            U16(0);                  // 36 internal attrs ← 少了这个就全盘错位
            U32(0);                  // 38 external attrs
            U32(localOffset);        // 42 local header offset
            w.Write(nameBytes);
        }
    }

    [Fact]
    public void 重复条目名应以后者为准()
    {
        using var zip = ZipReader.FromBuffer(
            BuildZipWithDuplicateNames("dup.txt", "first"u8.ToArray(), "second"u8.ToArray()));

        // Entries 里两条都在
        Assert.Equal(2, zip.Entries.Count(e => e.Name == "dup.txt"));

        // 但按名字查只得到一个 —— 与 Node 的 Map 同语义，后者覆盖前者
        var entry = zip.Entry("dup.txt");
        Assert.NotNull(entry);
        Assert.Equal("second", System.Text.Encoding.UTF8.GetString(zip.Read(entry!)));
    }

    // ==================================================================
    // 中央目录
    // ==================================================================

    [Fact]
    public void 纯文件zip不应产生目录条目()
    {
        // 【实测结论】PCL2 导出的包 316 条目里is_dir=True 的有 **0** 个 ——
        // 打包工具不写目录记录。所以 IsDir 的真实用途只是防御畸形包，
        // 不是常态路径。这里锁住「不误判」即可。
        using var zip = BuildZip("mods/a.jar", "config/b.txt");

        Assert.Equal(2, zip.Entries.Count);
        Assert.DoesNotContain(zip.Entries, e => e.IsDir);
    }

    [Fact]
    public void 名字以斜杠结尾的条目应判为目录()
    {
        // 用 BuildMinimalZip 造一条 0 字节、以 / 结尾的条目
        var bytes = BuildMinimalZip(
            Encoding.UTF8.GetBytes("mods/"),
            utf8Flag: true,
            content: []);

        using var zip = ZipReader.FromBuffer(bytes);

        var entry = zip.Entries[0];
        Assert.Equal("mods/", entry.Name);
        Assert.True(entry.IsDir);
        // 目录条目读出来是空数组，不是异常
        Assert.Empty(zip.Read(entry));
    }

    [Fact]
    public void 垃圾数据应抛明确异常而非静默返回空()
    {
        // 太小，连 EOCD 都放不下
        Assert.Throws<InvalidDataException>(() =>
        {
            using var zip = ZipReader.FromBuffer(new byte[10]);
            _ = zip.Entries;
        });

        // 长度够但没有 EOCD 签名
        Assert.Throws<InvalidDataException>(() =>
        {
            using var zip = ZipReader.FromBuffer(new byte[64]);
            _ = zip.Entries;
        });

        // 有 PK 开头但整体是垃圾：找不到 EOCD 也要明确报错
        var junk = new byte[128];
        junk[0] = 0x50; junk[1] = 0x4B; junk[2] = 0x03; junk[3] = 0x04;
        Assert.Throws<InvalidDataException>(() =>
        {
            using var zip = ZipReader.FromBuffer(junk);
            _ = zip.Entries;
        });
    }

    [Fact]
    public void 非zip文件应报错而不是崩溃()
    {
        // 一个真实的非 zip 文件（本测试 DLL 必然不是 zip）
        var path = Path.Combine(AppContext.BaseDirectory, "McUpdater.Tests.dll");
        if (!File.Exists(path))
        {
            return;
        }

        Assert.Throws<InvalidDataException>(() =>
        {
            using var zip = ZipReader.FromFile(path);
            _ = zip.Entries;
        });
    }

    [Fact]
    public void 读取不存在的条目应抛KeyNotFound()
    {
        using var zip = BuildZip("a.txt");
        Assert.Throws<KeyNotFoundException>(() => zip.Read("nope.txt"));
    }

    // ==================================================================
    // STORED vs DEFLATE
    // ==================================================================

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]   // STORED，method=0
    [InlineData(CompressionLevel.Optimal)]         // DEFLATE，method=8
    public void 两种压缩方式都应正确往返(CompressionLevel level)
    {
        // 内容要有一定长度，否则 NoCompression 也会走 DEFLATE 分支
        var payload = Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Range(0, 500).Select(i => $"line {i}: 中文内容测试\r\n")));

        using var ms = new MemoryStream();
        using (var za = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = za.CreateEntry("payload.txt", level);
            using var w = e.Open();
            w.Write(payload);
        }

        ms.Position = 0;
        using var zip = ZipReader.FromBuffer(ms.ToArray());

        var entry = zip.Entry("payload.txt");
        Assert.NotNull(entry);
        Assert.Equal(payload.Length, entry!.Size);
        Assert.Equal(FsHelpers.Crc32(payload), entry.Crc32);
        Assert.Equal(payload, zip.Read(entry));
    }

    [Fact]
    public void STORED条目的压缩大小应等于原始大小()
    {
        var payload = Encoding.UTF8.GetBytes("stored content 测试");
        using var ms = new MemoryStream();
        using (var za = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = za.CreateEntry("s.bin", CompressionLevel.NoCompression);
            using var w = e.Open();
            w.Write(payload);
        }
        ms.Position = 0;
        using var zip = ZipReader.FromBuffer(ms.ToArray());
        var entry = zip.Entry("s.bin")!;

        Assert.Equal(0, entry.Method);
        Assert.Equal(entry.Size, entry.CompSize);
    }

    [Fact]
    public void 指纹格式应为size冒号crc()
    {
        var payload = Encoding.UTF8.GetBytes("fingerprint");
        using var zip = BuildZip("f.bin");
        var entry = zip.Entries[0];
        Assert.Equal($"{entry.Size}:{entry.Crc32}", entry.Fingerprint);
    }

    // ==================================================================
    // 大文件落盘
    // ==================================================================

    [Fact]
    public void ExtractToFile应产出与内存读取一致的内容()
    {
        var payload = Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Range(0, 20000).Select(i => $"row-{i}\n")));

        using var zip = BuildZip(("big/data.bin", payload));

        var tmp = Path.Combine(Path.GetTempPath(), $"mcup_zip_{Guid.NewGuid():N}.bin");
        try
        {
            zip.ExtractToFile("big/data.bin", tmp);

            Assert.Equal(payload.Length, new FileInfo(tmp).Length);
            Assert.Equal(payload, File.ReadAllBytes(tmp));
            // 落盘后 CRC 仍应与中央目录一致 —— 这是 diff 指纹可信的基础
            Assert.Equal(zip.Entry("big/data.bin")!.Crc32, FsHelpers.Crc32OfFile(tmp));
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    // ==================================================================
    // 生命周期
    // ==================================================================

    [Fact]
    public void Dispose后操作应抛ObjectDisposed()
    {
        var zip = BuildZip("a.txt");
        zip.Dispose();
        zip.Dispose();  // 幂等，不该抛

        Assert.Throws<ObjectDisposedException>(() => zip.Read("a.txt"));
    }

    // ==================================================================
    // 辅助
    // ==================================================================

    /// <summary>
    /// 造一个内存 zip（用 <see cref="ZipArchive"/>，条目名按名字内容自动定标志位）。
    /// <b>要压GBK 回退分支请改用 <see cref="BuildMinimalZip"/></b>。
    /// </summary>
    private static ZipReader BuildZip(params string[] names)
        => BuildZip(names.Select(n => (n, Array.Empty<byte>())).ToArray());

    private static ZipReader BuildZip(params (string Name, byte[] Data)[] items)
    {
        var ms = new MemoryStream();
        using (var za = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in items)
            {
                var entry = za.CreateEntry(name, CompressionLevel.Optimal);
                using var w = entry.Open();
                w.Write(data.Length > 0 ? data : Encoding.UTF8.GetBytes("内容"));
            }
        }
        return ZipReader.FromBuffer(ms.ToArray());
    }
}
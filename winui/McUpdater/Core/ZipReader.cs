using System.IO.Compression;
using System.Text;

namespace McUpdater.Core;

/// <summary>一个 zip 条目的元信息。</summary>
public sealed class ZipEntry
{
    /// <summary>条目名（<c>/</c> 分隔，目录以 <c>/</c> 结尾）。</summary>
    public required string Name { get; init; }

    /// <summary>是否是目录条目。</summary>
    public required bool IsDir { get; init; }

    /// <summary>压缩方式：0 = STORED，8 = DEFLATE。</summary>
    public required int Method { get; init; }

    /// <summary>CRC32（无符号）。<b>这是 diff 指纹的命脉</b>，见 <see cref="Fingerprint"/>。</summary>
    public required uint Crc32 { get; init; }

    /// <summary>压缩后字节数。</summary>
    public required long CompSize { get; init; }

    /// <summary>解压后字节数。</summary>
    public required long Size { get; init; }

    /// <summary>本条目在文件里的起始偏移（本地文件头的位置）。</summary>
    public required long LocalOffset { get; init; }

    /// <summary>最后修改时间（UTC）。解析失败为 <see cref="DateTime.UnixEpoch"/>。</summary>
    public DateTime MTime { get; init; }

    /// <summary>
    /// 指纹 = <c>size:crc32</c>。
    ///
    /// <para><b>这是整个 diff 引擎的性能命脉</b>：中央目录里自带 CRC32，
    /// 所以比对两个包时可以对绝大多数条目只做字符串比较就判定「字节级一致」，
    /// 完全跳过解压。反过来，若这里的 CRC32 与文件实际内容对不上，
    /// 所有 unchanged 会被误判成 updated。</para>
    /// </summary>
    public string Fingerprint => $"{Size}:{Crc32}";

    public override string ToString() => $"{Name} ({Size} bytes, m={Method}, crc={Crc32:X8})";
}

/// <summary>
/// 零依赖 ZIP 读取器（移植自 Node 版 <c>src/core/zip-reader.js</c>）。
///
/// <para><b>为什么不直接用 <c>System.IO.Compression.ZipArchive</c></b>：
/// 它不暴露「条目自带的 CRC32」，也不暴露「数据区起始偏移」。
/// 而这两样正是本项目的命脉——CRC32 用于 diff 指纹预筛，
/// 数据区偏移让 STORED 条目可以零解压直读。用 ZipArchive 就得把每个
/// 条目解压一遍才能拿到同样的信息：真实样本 90 个 mod、单个最大 84MB，
/// 全解压会直接把内存打爆。</para>
///
/// <para><b>真实样本验证</b>（只读打开，不修改原文件）：
/// <c>1.21.11 1.0.6.zip</c>，226MB / 316 条目，其中 90 个 mod 含
/// <c>[JEI物品管理器]</c> 这类 PCL2 中文前缀名，正是文件名解码的试金石。</para>
///
/// <para><b>移植取舍</b>：Node 版 8 个方法，这里留 6 个。
/// <c>extractToFile</c> 保留（M3 的嵌套 mrpack 落盘缓存要它，
/// 且它顺带解决大 jar 不进内存）；<c>openNestedViaTempFile</c> 不留
/// （那是 modpack-reader 的调用方细节，跟着它一起走）。</para>
/// </summary>
public sealed class ZipReader : IDisposable
{
    private const uint SigLfh = 0x04034B50;        // Local File Header
    private const uint SigCdh = 0x02014B50;        // Central Directory Header
    private const uint SigEocd = 0x06054B50;       // End of Central Directory
    private const uint SigEocd64 = 0x06064B50;     // ZIP64 EOCD
    private const uint SigEocd64Loc = 0x07064B50;  // ZIP64 EOCD Locator

    private const int MaxComment = 0xFFFF;
    private const int EocdMin = 22;
    private const int LfhFixedLen = 30;

    /// <summary>GBK 编码。用前先经 <see cref="EnsureGbk"/> 确保 provider 已注册。</summary>
    private static Encoding? _gbk;

    /// <summary>
    /// 确保 GBK 编码可用（幂等）。
    ///
    /// <para><b>为什么必须显式注册</b>：.NET Core / .NET 5+ 只内置 UTF-8 与
    /// ASCII，936 编码表在 <c>System.Text.Encoding.CodePages</c> 里，
    /// 必须注册 <see cref="CodePagesEncodingProvider"/> 才能用。
    /// 不注册时 <c>GetEncoding(936)</c> 抛 <c>NotSupportedException</c>。</para>
    ///
    /// <para>暴露成 public 是因为<em>测试要构造 GBK 字节</em>——
    /// 若只有内部能注册，外部测试就得自己再注册一遍，容易漏。</para>
    /// </summary>
    public static void EnsureGbk()
    {
        if (_gbk is not null)
        {
            return;
        }
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _gbk = Encoding.GetEncoding(936);
        }
        catch
        {
            _gbk = null;
        }
    }

    private static Encoding? Gbk
    {
        get
        {
            EnsureGbk();
            return _gbk;
        }
    }

    private readonly FileStream? _fs;
    private readonly byte[]? _buf;
    private readonly long _size;
    /// <summary>子区间读基址：用于「外层 zip 里以 STORED 存放的内层 zip」。</summary>
    private readonly long _base;

    private List<ZipEntry>? _entries;
    private Dictionary<string, ZipEntry>? _byName;
    private bool _disposed;

    private ZipReader(FileStream? fs, byte[]? buf, long size, long start)
    {
        _fs = fs;
        _buf = buf;
        _size = size;
        _base = start;
    }

    /// <summary>基于文件路径打开（低内存，大包首选）。</summary>
    public static ZipReader FromFile(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, options: FileOptions.RandomAccess);
        return new ZipReader(fs, null, fs.Length, 0);
    }

    /// <summary>基于内存字节数组打开（用于嵌套 zip / jar）。</summary>
    public static ZipReader FromBuffer(byte[] buffer)
        => new(null, buffer, buffer.Length, 0);

    /// <summary>
    /// 基于文件的某个字节区间打开。
    ///
    /// <para><b>场景</b>：Modrinth 的 <c>.mrpack</c> 本体被塞在另一个 zip 里，
    /// 且压缩方式是 STORED —— 此时内层是<em>连续裸字节</em>，
    /// 不必解压就能直接读它的中央目录，零临时文件。</para>
    /// </summary>
    public static ZipReader FromFileRange(string path, long start, long length)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, options: FileOptions.RandomAccess);
        return new ZipReader(fs, null, length, start);
    }

    // ------------------------------------------------------------------
    // 中央目录
    // ------------------------------------------------------------------

    /// <summary>
    /// 解析全部条目（惰性，只做一次）。
    ///
    /// <para>只读文件尾部几十 KB 就能拿到全部条目信息，不用读整个文件 ——
    /// 226MB 的包，拿到全部 316 条目录表是毫秒级。</para>
    /// </summary>
    public IReadOnlyList<ZipEntry> Entries
    {
        get
        {
            _entries ??= ParseCentralDirectory();
            return _entries;
        }
    }

    private List<ZipEntry> ParseCentralDirectory()
    {
        if (_size < EocdMin)
        {
            throw new InvalidDataException("文件太小，不是有效的 ZIP");
        }

        // --- 1. 反向搜索 EOCD ---
        var tailLen = (int)Math.Min(_size, MaxComment + EocdMin);
        var tail = ReadRange(_size - tailLen, tailLen);

        var eocd = -1;
        for (var i = tail.Length - EocdMin; i >= 0; i--)
        {
            if (ReadU32(tail, i) == SigEocd)
            {
                eocd = i;
                break;
            }
        }
        if (eocd < 0)
        {
            throw new InvalidDataException("未找到 ZIP 结束标记，文件可能已损坏或不是 ZIP");
        }

        var entryCount = (long)ReadU16(tail, eocd + 10);
        var cdSize = ReadU32(tail, eocd + 12);
        var cdOffset = (long)ReadU32(tail, eocd + 16);

        // --- 2. ZIP64：三个字段任一为哨兵值就要往 64 位找 ---
        //     哨兵值 0xFFFF / 0xFFFFFFFF 是「真值装不下，去 ZIP64 结构里取」
        if (entryCount == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF)
        {
            var locRel = eocd - 20;
            if (locRel >= 0 && ReadU32(tail, locRel) == SigEocd64Loc)
            {
                var z64Off = (long)ReadU64(tail, locRel + 8);
                var z64 = ReadRange(z64Off, 56);
                if (z64.Length >= 56 && ReadU32(z64, 0) == SigEocd64)
                {
                    entryCount = (long)ReadU64(z64, 32);
                    cdSize = (uint)ReadU64(z64, 40);
                    cdOffset = (long)ReadU64(z64, 48);
                }
            }
        }

        // --- 3. 遍历中央目录 ---

        // 【必须先做边界校验，不能直接 ReadRange】
        // 中央目录里的 cdOffset / cdSize 是**文件自己声明的**，可以撒谎。
        // 喂一个非 zip 文件（比如误选了一个 .jar 或 .exe）时，文件里
        // 偶然出现的 PK\x05\x06 字节会被当成 EOCD，后面的 cdSize 便是垃圾值
        // （实测某 DLL 触发 OverflowException —— new byte[垃圾值] 直接炸）。
        //
        // Node 版这里用 subarray，超界会**静默截断**成短数组；
        // C# 必须显式拦，否则要么 Overflow 要么吃内存。
        if (cdOffset < 0 || cdOffset > _size)
        {
            throw new InvalidDataException(
                $"中央目录偏移越界（offset={cdOffset}, 文件大小={_size}），文件不是有效的 ZIP");
        }
        if (cdSize > _size - cdOffset)
        {
            throw new InvalidDataException(
                $"中央目录长度越界（size={cdSize}, 偏移={cdOffset}, 文件大小={_size}），文件已损坏");
        }

        var cd = ReadRange(cdOffset, cdSize);
        // 条目数可能被恶意/损坏的 zip 谎报成天文数字，
        // 按中央目录实际长度封顶，避免 List 预分配就吃掉几个 GB。
        var capacity = (int)Math.Min(entryCount, Math.Max(0, cd.Length / 46));
        var list = new List<ZipEntry>(capacity);
        var p = 0;

        for (var n = 0; n < entryCount; n++)
        {
            if (p + 46 > cd.Length || ReadU32(cd, p) != SigCdh)
            {
                break;
            }

            var flags = ReadU16(cd, p + 8);
            var method = ReadU16(cd, p + 10);
            var dosTime = ReadU16(cd, p + 12);
            var dosDate = ReadU16(cd, p + 14);
            var crc = ReadU32(cd, p + 16);
            var compSize = (long)ReadU32(cd, p + 20);
            var size = (long)ReadU32(cd, p + 24);
            var nameLen = ReadU16(cd, p + 28);
            var extraLen = ReadU16(cd, p + 30);
            var commentLen = ReadU16(cd, p + 32);
            var externalAttr = ReadU32(cd, p + 38);
            var localOffset = (long)ReadU32(cd, p + 42);

            var name = DecodeName(cd.AsSpan(p + 46, nameLen), (flags & 0x800) != 0);

            // ZIP64 extra field：按需取回被替换成哨兵值的字段
            if (size == 0xFFFFFFFF || compSize == 0xFFFFFFFF || localOffset == 0xFFFFFFFF)
            {
                var e = p + 46 + nameLen;
                var end = e + extraLen;
                while (e + 4 <= end)
                {
                    var hid = ReadU16(cd, e);
                    var hsz = ReadU16(cd, e + 2);
                    if (hid == 0x0001)
                    {
                        var q = e + 4;
                        if (size == 0xFFFFFFFF) { size = (long)ReadU64(cd, q); q += 8; }
                        if (compSize == 0xFFFFFFFF) { compSize = (long)ReadU64(cd, q); q += 8; }
                        if (localOffset == 0xFFFFFFFF) { localOffset = (long)ReadU64(cd, q); }
                        break;
                    }
                    e += 4 + hsz;
                }
            }

            // 目录判定：名字以 / 结尾，或 externalAttr 的目录位(0x10)置位且大小为 0
            var isDir = name.EndsWith('/') || ((externalAttr & 0x10) != 0 && size == 0);

            list.Add(new ZipEntry
            {
                Name = name,
                IsDir = isDir,
                Method = method,
                Crc32 = crc,
                CompSize = compSize,
                Size = size,
                LocalOffset = localOffset,
                MTime = DosToDateTime(dosTime, dosDate),
            });

            p += 46 + nameLen + extraLen + commentLen;
        }

        _byName = new Dictionary<string, ZipEntry>(list.Count, StringComparer.Ordinal);
        foreach (var e in list)
        {
            // 与 Node 的 Map 同语义：后者覆盖前者
            _byName[e.Name] = e;
        }
        return list;
    }

    // ------------------------------------------------------------------
    // 查表
    // ------------------------------------------------------------------

    /// <summary>按名字取条目，不存在返回 <c>null</c>。</summary>
    public ZipEntry? Entry(string name)
    {
        if (_byName is null)
        {
            _ = Entries;
        }
        return _byName!.GetValueOrDefault(name);
    }

    /// <summary>是否存在指定条目。</summary>
    public bool Has(string name) => Entry(name) is not null;

    // ------------------------------------------------------------------
    // 读取
    // ------------------------------------------------------------------

    /// <summary>
    /// 计算压缩数据的真实起始偏移。
    ///
    /// <para><b>为什么不能直接用 <c>LocalOffset + 30</c></b>：本地文件头的
    /// 变长部分是文件名 + extra field，长度由这个头自己携带（偏移 26 / 28 处
    /// 各 2 字节），各不相同。真实样本里 <c>overrides/mods/</c> 下有
    /// 超长中文名（<c>[MrCrayfish 的家具：重制] refurbished_furniture-...</c>），
    /// 按固定偏移算会全部错位。</para>
    /// <para><b>internal 而非 private</b>：<see cref="ModpackReader"/> 的 PCL2
    /// 快速路径要拿到内层 <c>.mrpack</c> 的数据起始偏移，再用
    /// <see cref="FromFileRange"/> 以子区间方式再开一个 reader（零解压）。
    /// Node 版对应 <c>outer._dataOffset(mrEnt)</c>。同程序集可见即可，
    /// 不扩到 public —— 那是 zip 实现细节，外部没有正当理由调它。</para>
    /// </summary>
    internal long DataOffset(ZipEntry entry)
    {
        var h = ReadRange(entry.LocalOffset, LfhFixedLen);
        if (h.Length < LfhFixedLen || ReadU32(h, 0) != SigLfh)
        {
            throw new InvalidDataException($"条目 \"{entry.Name}\" 的本地头损坏");
        }
        return entry.LocalOffset + LfhFixedLen + ReadU16(h, 26) + ReadU16(h, 28);
    }

    /// <summary>读取并解压单个条目。目录条目返回空数组。</summary>
    public byte[] Read(string name)
    {
        var entry = Entry(name) ?? throw new KeyNotFoundException($"条目不存在: {name}");
        return Read(entry);
    }

    /// <summary>读取并解压单个条目。目录条目返回空数组。</summary>
    public byte[] Read(ZipEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (entry.IsDir)
        {
            return [];
        }

        var off = DataOffset(entry);

        if (entry.Method == 0)
        {
            // STORED：数据区就是明文，直接裸读
            return ReadRange(off, entry.CompSize);
        }
        if (entry.Method == 8)
        {
            var raw = ReadRange(off, entry.CompSize);
            using var input = new MemoryStream(raw, writable: false);
            // DeflateStream 读的就是 raw deflate，对应 Node 的 zlib.inflateRawSync
            using var inflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream((int)Math.Min(entry.Size, int.MaxValue));
            inflate.CopyTo(output);
            return output.ToArray();
        }
        throw new NotSupportedException($"不支持的压缩方式 {entry.Method}（条目 {entry.Name}）");
    }

    /// <summary>读取条目内容并按 UTF-8 解码。</summary>
    public string ReadText(string name) => Encoding.UTF8.GetString(Read(name));

    /// <summary>
    /// 把条目解压到磁盘文件。
    ///
    /// <para><b>大文件必须走这条路</b>：真实样本里单个 mod 84MB
    /// （<c>AmbientSounds_FABRIC_v6.3.5</c>），整块进内存再写盘是纯浪费。
    /// STORED 条目可以直接把区间拷过去，连解压都省了。</para>
    /// </summary>
    public void ExtractToFile(string name, string destPath)
    {
        var entry = Entry(name) ?? throw new KeyNotFoundException($"条目不存在: {name}");
        ExtractToFile(entry, destPath);
    }

    /// <inheritdoc cref="ExtractToFile(string, string)"/>
    public void ExtractToFile(ZipEntry entry, string destPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var off = DataOffset(entry);

        if (entry.Method == 0)
        {
            CopyRange(off, entry.CompSize, destPath);
            return;
        }
        if (entry.Method != 8)
        {
            throw new NotSupportedException($"不支持的压缩方式 {entry.Method}（条目 {entry.Name}）");
        }

        using var src = OpenRange(off, entry.CompSize);
        using var inflate = new DeflateStream(src, CompressionMode.Decompress);
        using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        inflate.CopyTo(dst);
    }

    // ------------------------------------------------------------------
    // 低层区间读
    // ------------------------------------------------------------------

    private Stream OpenRange(long offset, long length)
    {
        // 内存模式：从底层数组切一段包装成流
        if (_buf is not null)
        {
            if (offset + length > _buf.Length)
            {
                throw new InvalidDataException("内存模式读取越界");
            }
            return new BoundedStream(new MemoryStream(_buf, false), offset, length, skipToOffset: true);
        }

        // 文件模式：开**独立** FileStream 而不是共享 _fs 再 Seek。
        // 前者天然并发安全 —— 将来若并行解析多个 jar，
        // 共享句柄会互相打断定位，症状是随机的解压失败。
        var fs = new FileStream(_fs!.Name, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, options: FileOptions.RandomAccess);
        fs.Position = _base + offset;
        return new BoundedStream(fs, 0, length, skipToOffset: false);
    }

    private byte[] ReadRange(long offset, long length)
    {
        if (length <= 0)
        {
            return [];
        }

        if (_buf is not null)
        {
            if (offset < 0 || offset + length > _buf.Length)
            {
                throw new InvalidDataException(
                    $"内存模式读取越界（offset={offset}, length={length}, 容量={_buf.Length}）");
            }
            return _buf.AsSpan((int)offset, (int)length).ToArray();
        }

        // 【文件模式也要先查边界，不能直接 new byte[length]】
        // length 来自文件自述，损坏或伪造的包会给出天文数字，
        // new byte[天文字数] 直接抛 OverflowException ——
        // 而调用方期待的是「这不是有效 zip」这种可读的错。
        if (offset < 0 || length < 0 || offset + length > _size)
        {
            throw new InvalidDataException(
                $"读取越界（offset={offset}, length={length}, 可用={_size}），文件已损坏");
        }

        var data = new byte[length];
        ObjectDisposedException.ThrowIf(_disposed, this);
        _fs!.Position = _base + offset;
        // ReadExactly 而非 Read：短读（尤其大文件）必须补齐，
        // 否则会把不完整的内容当成完整条目用，CRC 校验才暴露问题，
        // 但那时已经晚了。
        _fs.ReadExactly(data);
        return data;
    }

    private void CopyRange(long offset, long length, string destPath)
    {
        using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        if (length <= 0)
        {
            return;
        }

        if (_buf is not null)
        {
            dst.Write(_buf, (int)offset, (int)length);
            return;
        }

        _fs!.Position = _base + offset;
        var buf = new byte[Math.Min(length, 1024 * 1024)];
        var remaining = length;
        while (remaining > 0)
        {
            var n = (int)Math.Min(buf.Length, remaining);
            _fs.ReadExactly(buf, 0, n);
            dst.Write(buf, 0, n);
            remaining -= n;
        }
    }

    // ------------------------------------------------------------------
    // 文件名解码
    // ------------------------------------------------------------------

    private static string DecodeName(ReadOnlySpan<byte> bytes, bool utf8Flag)
    {
        if (utf8Flag)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        // 未标 UTF-8：先按 UTF-8 严格试，失败再用 GBK。
        // 真实样本的 PCL2 中文名前缀走的就是这条回退路径。
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            if (Gbk is not null)
            {
                try
                {
                    return Gbk.GetString(bytes);
                }
                catch
                {
                    // 落到 latin1：与 Node 一致，保证永不抛异常
                }
            }
            return Encoding.Latin1.GetString(bytes);
        }
    }

    // ------------------------------------------------------------------
    // 小工具
    // ------------------------------------------------------------------

    private static DateTime DosToDateTime(int dosTime, int dosDate)
    {
        try
        {
            return new DateTime(
                ((dosDate >> 9) & 0x7F) + 1980,
                ((dosDate >> 5) & 0x0F),
                dosDate & 0x1F,
                (dosTime >> 11) & 0x1F,
                (dosTime >> 5) & 0x3F,
                (dosTime & 0x1F) * 2,
                DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.UnixEpoch;
        }
    }

    private static ushort ReadU16(ReadOnlySpan<byte> b, int o)
        => (ushort)(b[o] | (b[o + 1] << 8));

    private static uint ReadU32(ReadOnlySpan<byte> b, int o)
        => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    private static ulong ReadU64(ReadOnlySpan<byte> b, int o)
        => ReadU32(b, o) | ((ulong)ReadU32(b, o + 4) << 32);

    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _fs?.Dispose();
        _entries = null;
        _byName = null;
    }

    /// <summary>
    /// 只读的定长字节流包装 —— 让 <see cref="ExtractToFile"/> 能把压缩数据
    /// 以流的方式喂给 <see cref="DeflateStream"/>，而不必整块读进内存。
    /// </summary>
    private sealed class BoundedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _start;
        private readonly long _length;
        private long _pos;

        /// <param name="skipToOffset">
        /// 内层流是否已经定位到 <paramref name="start"/>。
        /// 文件模式我们已手动设好 Position，所以传 false；
        /// 内存模式传 true，让本类自己去跳过。
        /// </param>
        public BoundedStream(Stream inner, long start, long length, bool skipToOffset)
        {
            _inner = inner;
            _start = start;
            _length = length;
            if (skipToOffset && start > 0 && inner.CanSeek)
            {
                inner.Seek(start, SeekOrigin.Begin);
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _pos;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = (int)Math.Min(buffer.Length, _length - _pos);
            if (n <= 0)
            {
                return 0;
            }
            var read = _inner.Read(buffer[..n]);
            _pos += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
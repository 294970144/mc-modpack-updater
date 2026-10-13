using System.Text;
using McUpdater.Core;
using Xunit;

namespace McUpdater.Tests;

/// <summary>
/// 真实整合包回归测试。
///
/// <para><b>样本</b>：<c>1.21.11 1.0.6.zip</c>（Master 2026-10-09 提供，
/// 路径 <c>D:/Users/zhang/Downloads/</c>，只读打开）。226MB / 316 条目，
/// Modrinth 全量打包格式（<c>files: []</c>，内容全在 <c>overrides/</c>）。</para>
///
/// <para><b>为什么这个样本格外有价值</b>——它同时踩中了三条最容易写错的路径：
/// <list type="bullet">
/// <item><b>中文文件名</b>：90 个 mod 里大量 PCL2 中文前缀
///       （<c>[JEI物品管理器] jei-1.21.11-fabric-27.17.0.50.jar</c>），
///       且这些条目很可能没标 UTF-8 标志位 → 走 GBK 回退解码</item>
/// <item><b>STORED 条目</b>：<c>overrides/config/animatica.properties</c>
///       的 compress_type 是 0，必须能零解压直读</item>
/// <item><b>超变长本地头</b>：中文名让本地头的 filenameLength 远超 ASCII 名，
///       DataOffset 算错就会整片读崩</item></list>
///
/// <para><b>测试不写原文件</b>：全部只读操作，用完即弃。</para>
/// </summary>
public sealed class RealPackTests
{
    private const string PackPath = @"D:\Users\zhang\Downloads\1.21.11 1.0.6.zip";

    private static bool PackExists => File.Exists(PackPath);

    private static ZipReader OpenPack()
    {
        // FileShare.Read：允许其他进程读，但不自己写。
        // 绝不以任何形式打开写入模式 —— 样本是 Master 的原始文件。
        return ZipReader.FromFile(PackPath);
    }

    // ==================================================================
    // 中央目录
    // ==================================================================

    [Fact]
    public void 真实包_中央目录应解析出全部条目()
    {
        if (!PackExists) return;   // 样本不在本机则放过

        using var zip = OpenPack();
        var entries = zip.Entries;

        Assert.Equal(316, entries.Count);
        Assert.All(entries, e => Assert.NotEmpty(e.Name));

        // 目录条目不该混进来当文件
        Assert.DoesNotContain(entries, e => e.IsDir && !e.Name.EndsWith('/'));
    }

    [Fact]
    public void 真实包_顶层结构应为Modrinth全量打包格式()
    {
        if (!PackExists) return;   // 样本不在本机则放过

        using var zip = OpenPack();
        var names = zip.Entries.Select(e => e.Name).ToList();

        // 标准 Modrinth 布局
        Assert.Contains("modrinth.index.json", names);
        Assert.Equal(315, names.Count(n => n.StartsWith("overrides/", StringComparison.Ordinal)));

        // 全量打包：files 为空，不依赖联网下载
        var indexJson = zip.ReadText("modrinth.index.json");
        Assert.Contains("\"files\": []", indexJson);
        Assert.Contains("\"versionId\": \"1.0.6\"", indexJson);
        Assert.Contains("\"fabric-loader\": \"0.19.3\"", indexJson);
    }

    // ==================================================================
    // 文件名解码（中文回退路径）
    // ==================================================================

    [Fact]
    public void 真实包_PCL2中文前缀文件名应正确解码()
    {
        if (!PackExists) return;   // 样本不在本机则放过

        using var zip = OpenPack();
        var modNames = zip.Entries
            .Where(e => e.Name.StartsWith("overrides/mods/", StringComparison.Ordinal))
            .Select(e => e.Name["overrides/mods/".Length..])
            .ToList();

        Assert.Equal(90, modNames.Count);

        // 这三条是最刁钻的：文件名含中文、方括号、冒号、空格，emoji 还是 4 字节 UTF-8
        Assert.Contains("[JEI物品管理器] jei-1.21.11-fabric-27.17.0.50.jar", modNames);
        Assert.Contains("[MrCrayfish 的家具：重制] refurbished_furniture-fabric-1.21.11-1.0.23.jar", modNames);
        Assert.Contains("[玉 🔍] Jade-1.21.11-Fabric-21.1.6.jar", modNames);

        // 【实测结论，别被想当然带偏】
        // 本样本 19 个含非 ASCII 的条目**全部标了 UTF-8 标志位（bit 11）**，
        // 所以它们全都走「直接 UTF-8 解码」这条路 —— 一条都没落到 GBK 回退上。
        // 换句话说：<b>这个样本压不到 GBK 分支</b>，别以为跑绿了就等于验证过 GBK。
        // GBK 回退要靠 ZipReaderTests 里手工构造的条目来覆盖
        //（国内老整合包、PCL2 早期版本确实会不标标志位）。
        Assert.Equal(19, zip.Entries.Count(e => !e.IsDir && e.Name.Any(c => c > 127)));

        // 解码失败会退化成 latin1 或出现 U+FFFD，中文会变成乱码
        foreach (var n in modNames)
        {
            Assert.DoesNotContain('�', n);
        }
    }

    [Fact]
    public void 真实包_中文条目应可按名字精确查回()
    {
        if (!PackExists) return;   // 样本不在本机则放过

        using var zip = OpenPack();
        const string target = "overrides/mods/[JEI物品管理器] jei-1.21.11-fabric-27.17.0.50.jar";

        var entry = zip.Entry(target);
        Assert.NotNull(entry);
        Assert.Equal(target, entry!.Name);
        Assert.Equal(1667656, entry.Size);

        // 大小不对说明本地头偏移算错（中文名导致 filenameLength 很大）
        Assert.True(entry.Size > 0);
        Assert.True(entry.CompSize > 0);
    }

    // ==================================================================
    // CRC32 —— diff 指纹的命脉
    // ==================================================================

    [Fact]
    public void 真实包_中央目录CRC应与实际内容一致()
    {
        if (!PackExists) return;   // 样本不在本机则放过

        using var zip = OpenPack();

        // 取各类压缩方式的条目各若干，覆盖 STORED 与 DEFLATE 两条路径。
        // 这是最关键的一条断言：CRC 对不上意味着所有 unchanged
        // 会被误判成 updated，diff 结果全是假的。
        var stored = zip.Entries.First(e => !e.IsDir && e.Method == 0);
        var deflated = zip.Entries.First(e => !e.IsDir && e.Method == 8 && e.Size < 2_000_000);

        foreach (var entry in new[] { stored, deflated })
        {
            var data = zip.Read(entry);
            Assert.Equal(entry.Size, data.LongLength);
            Assert.Equal(entry.Crc32, FsHelpers.Crc32(data));
        }
    }

    [Fact]
    public void 真实包_STORED条目应零解压直读()
    {
        if (!PackExists) return;   // 样本不在本机则放过

        using var zip = OpenPack();
        var entry = zip.Entries.First(e => !e.IsDir && e.Method == 0);

        // STORED 的 compSize 必须等于 size —— 这才说明能裸读
        Assert.Equal(entry.Size, entry.CompSize);

        var data = zip.Read(entry);
        Assert.Equal(entry.Size, data.LongLength);
        Assert.Equal(entry.Crc32, FsHelpers.Crc32(data));
    }

    // ==================================================================
    // 读内容
    // ==================================================================

    [Fact]
    public void 真实包_应能读出文本条目内容()
    {
        if (!PackExists) return;   // 样本不在本机则放过

        using var zip = OpenPack();

        // options.txt 是纯文本，能验证解压链路 + 文本解码
        var text = zip.ReadText("overrides/options.txt");
        Assert.NotEmpty(text);
        // Minecraft 选项文件必有这几行
        Assert.Contains("version:", text);
    }

    [Fact]
    public void 真实包_jwt词条应能被内存模式打开()
    {
        if (!PackExists) return;   // 样本不在本机则放过

        // jar 本身是 zip —— 解一个小的 mod（4KB，最小最省时）
        using var pack = OpenPack();
        const string tiny = "overrides/mods/morechathistory-1.3.1.jar";

        var jarBytes = pack.Read(tiny);
        Assert.Equal(3573, jarBytes.Length);

        // 嵌套场景：jar 走内存模式
        using var jar = ZipReader.FromBuffer(jarBytes);
        Assert.NotEmpty(jar.Entries);

        // Fabric 模组必须带 fabric.mod.json
        Assert.True(jar.Has("fabric.mod.json"), "缺少 fabric.mod.json");
        Assert.True(jar.Has("META-INF/MANIFEST.MF"), "缺少 MANIFEST.MF");

        var fabricJson = Encoding.UTF8.GetString(jar.Read("fabric.mod.json"));
        Assert.Contains("\"schemaVersion\"", fabricJson);
    }

    [Fact]
    public void 真实包_大文件解压到磁盘应与内存一致()
    {
        if (!PackExists) return;   // 样本不在本机则放过

        using var zip = OpenPack();
        // 选一个 1MB 以上的 jar：足以走 CopyTo 的多轮循环
        var entry = zip.Entries.First(e =>
            e.Name.StartsWith("overrides/mods/", StringComparison.Ordinal)
            && e.Method == 8 && e.Size > 1_000_000);

        var tmp = Path.Combine(Path.GetTempPath(), $"mcup_zip_{Guid.NewGuid():N}.jar");
        try
        {
            zip.ExtractToFile(entry, tmp);

            var info = new FileInfo(tmp);
            Assert.Equal(entry.Size, info.Length);

            // 落盘内容的 CRC 必须仍等于中央目录里存的那个
            Assert.Equal(entry.Crc32, FsHelpers.Crc32OfFile(tmp));
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    [Fact]
    public void 真实包_清单应覆盖到四个受管分类()
    {
        if (!PackExists) return;   // 样本不在本机则放过

        using var zip = OpenPack();
        var cats = new Dictionary<string, int>
        {
            ["config"] = 0, ["mods"] = 0, ["resourcepacks"] = 0, ["shaderpacks"] = 0,
        };

        foreach (var e in zip.Entries)
        {
            if (e.IsDir || !e.Name.StartsWith("overrides/", StringComparison.Ordinal))
            {
                continue;
            }
            var rest = e.Name["overrides/".Length..];
            var slash = rest.IndexOf('/');
            if (slash > 0 && cats.TryGetValue(rest[..slash], out var n))
            {
                cats[rest[..slash]] = n + 1;
            }
        }

        Assert.Equal(90, cats["mods"]);
        Assert.Equal(193, cats["config"]);
        Assert.Equal(8, cats["resourcepacks"]);
        Assert.Equal(3, cats["shaderpacks"]);
    }
}
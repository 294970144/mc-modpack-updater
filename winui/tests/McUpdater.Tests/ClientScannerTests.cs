using System.IO;
using System.Text;
using McUpdater.Core;

namespace McUpdater.Tests;

/// <summary>
/// M4：<see cref="ClientScanner"/> 的单元测试（合成目录，不碰真实游戏目录）。
///
/// <para>重点覆盖两块最容易写错、且写错了不会立刻报错的逻辑：
/// <b>隔离判定</b>（决定内容目录，猜错会把文件写到错误位置）与
/// <b>匹配打分</b>（决定"更新到哪个版本"，猜错更隐蔽）。</para>
/// </summary>
public class ClientScannerTests : IDisposable
{
    private readonly string _tmp;

    public ClientScannerTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "mcu-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* 清理失败无所谓 */ }
        GC.SuppressFinalize(this);
    }

    private static void Write(string path, string content = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    /// <summary>建一个 .minecraft：含一个版本目录。<paramref name="isoMods"/> 控制隔离态。</summary>
    private string MakeMc(string name, string versionName, bool isoMods, bool sharedMods,
        string? setup = null, string? pclIniVersion = null, bool manifest = false)
    {
        var mc = Path.Combine(_tmp, name);
        var ver = Path.Combine(mc, AppConfig.McVersionsDir, versionName);

        Write(Path.Combine(ver, versionName + ".json"), "{}");
        if (setup is not null)
        {
            Write(Path.Combine(ver, "PCL", "Setup.ini"), setup);
        }
        // 注意：.jar.disabled 也要算进 mod 数量（Node 的 isJar 正则带可选后缀）
        if (isoMods)
        {
            Write(Path.Combine(ver, "mods", "a.jar"));
            Write(Path.Combine(ver, "mods", "b.jar.disabled"));
            Write(Path.Combine(ver, "mods", "not-a-mod.txt"));
        }
        if (sharedMods)
        {
            Write(Path.Combine(mc, "mods", "shared.jar"));
        }
        if (manifest)
        {
            Write(Path.Combine(ver, AppConfig.ManifestName), "{}");
        }
        if (pclIniVersion is not null)
        {
            Write(Path.Combine(mc, AppConfig.McPclIni), $"InstanceCache:1\r\nVersion:{pclIniVersion}\r\n");
        }
        return mc;
    }

    // ------------------------------------------------------------------
    // 隔离判定
    // ------------------------------------------------------------------

    [Fact]
    public void 隔离_版本目录有mod而共享目录没有_判为隔离()
    {
        var mc = MakeMc("iso", "1.21.11", isoMods: true, sharedMods: false);

        var v = Assert.Single(ClientScanner.ScanVersions(mc));
        Assert.True(v.Isolated);
        Assert.Equal("files", v.IsolationBasis);
        Assert.Equal(Path.Combine(mc, "versions", "1.21.11"), v.ContentDir);
    }

    [Fact]
    public void 隔离_共享目录有mod而版本目录没有_判为未隔离()
    {
        var mc = MakeMc("shared", "1.21.11", isoMods: false, sharedMods: true);

        var v = Assert.Single(ClientScanner.ScanVersions(mc));
        Assert.False(v.Isolated);
        Assert.Equal("files", v.IsolationBasis);
        // 【关键】内容目录回到 .minecraft 根 —— 写文件时用的就是这个目录
        Assert.Equal(mc, v.ContentDir);
    }

    [Fact]
    public void 隔离_两边都有mod_看配置项()
    {
        var mc = MakeMc("mixed", "1.21.11", isoMods: true, sharedMods: true,
            setup: "VersionArgumentIndie:0\r\n");

        var v = Assert.Single(ClientScanner.ScanVersions(mc));
        Assert.False(v.Isolated);
        Assert.Equal("mixed", v.IsolationBasis);
    }

    [Fact]
    public void 隔离_两边都空_取默认值隔离()
    {
        var mc = MakeMc("empty", "1.21.11", isoMods: false, sharedMods: false);

        var v = Assert.Single(ClientScanner.ScanVersions(mc));
        Assert.True(v.Isolated);
        Assert.Equal("default", v.IsolationBasis);
    }

    [Fact]
    public void 隔离_两边都空但配置写死_按配置走()
    {
        var mc = MakeMc("cfg", "1.21.11", isoMods: false, sharedMods: false,
            setup: "VersionArgumentIndieV2:False\r\n");

        var v = Assert.Single(ClientScanner.ScanVersions(mc));
        Assert.False(v.Isolated);
        Assert.Equal("config", v.IsolationBasis);
    }

    // ------------------------------------------------------------------
    // 版本扫描
    // ------------------------------------------------------------------

    [Fact]
    public void ScanVersions_mod计数只算jar含disabled后缀()
    {
        var mc = MakeMc("count", "1.21.11", isoMods: true, sharedMods: false,
            setup: "VersionFabric:0.19.3\r\nVersionVanillaName:1.21.11\r\nInfo:正式版 1.21.11, Fabric 0.19.3\r\n");

        var v = Assert.Single(ClientScanner.ScanVersions(mc));
        Assert.Equal(2, v.ModCount);              // a.jar + b.jar.disabled，txt 不算
        Assert.Equal("fabric", v.Loader);
        Assert.Equal("0.19.3", v.LoaderVersion);
        Assert.Equal("1.21.11", v.GameVersion);
        Assert.Equal("正式版 1.21.11, Fabric 0.19.3", v.Info);
        Assert.False(v.Managed);
    }

    [Fact]
    public void ScanVersions_非mods分类只记有没有()
    {
        var mc = MakeMc("cats", "1.21.11", isoMods: true, sharedMods: false);
        Directory.CreateDirectory(Path.Combine(mc, "versions", "1.21.11", "config"));
        Directory.CreateDirectory(Path.Combine(mc, "versions", "1.21.11", "resourcepacks"));

        var v = Assert.Single(ClientScanner.ScanVersions(mc));
        Assert.Equal(1, v.Counts["config"]);
        Assert.Equal(1, v.Counts["resourcepacks"]);
        Assert.Equal(0, v.Counts["shaderpacks"]);
    }

    [Fact]
    public void ScanVersions_没有同名json的目录不算版本()
    {
        var mc = Path.Combine(_tmp, "nover");
        Directory.CreateDirectory(Path.Combine(mc, "versions", "ghost"));

        Assert.Empty(ClientScanner.ScanVersions(mc));
    }

    [Fact]
    public void ScanVersions_有安装记录文件的算managed()
    {
        var mc = MakeMc("managed", "1.21.11", isoMods: true, sharedMods: false, manifest: true);

        Assert.True(Assert.Single(ClientScanner.ScanVersions(mc)).Managed);
    }

    [Fact]
    public void ScanVersions_PCLini标出的版本排在最前()
    {
        var mc = MakeMc("order", "aaa", isoMods: true, sharedMods: false, pclIniVersion: "zzz");
        MakeMc("order", "zzz", isoMods: true, sharedMods: false, pclIniVersion: "zzz");
        // MakeMc 第二次调用会重建 mc/PCL.ini（同参数），版本 zzz 被标记为选中

        var list = ClientScanner.ScanVersions(mc);
        Assert.Equal(2, list.Count);
        Assert.Equal("zzz", list[0].Name);
        Assert.True(list[0].IsSelected);
        Assert.False(list[1].IsSelected);
    }

    [Fact]
    public void ScanVersions_SetupIni缺失时从version_json推断加载器()
    {
        var mc = Path.Combine(_tmp, "jsonloader");
        var ver = Path.Combine(mc, "versions", "1.20.1");
        Write(Path.Combine(ver, "1.20.1.json"),
            """{"id":"1.20.1","libraries":[{"name":"net.fabricmc:fabric-loader:0.14.22"}]}""");

        var v = Assert.Single(ClientScanner.ScanVersions(mc));
        Assert.Equal("fabric", v.Loader);
        Assert.Null(v.LoaderVersion);
    }

    [Fact]
    public void ScanVersions_加载器优先级_fabric_neoforge_forge_quilt()
    {
        // 与 ModpackReader 那侧的优先级不同（那边是 fabric→quilt→forge→neoforge）。
        // Node 两个文件就是这么写的，各按各的来，别"统一"。
        var mc = MakeMc("multi", "1.21.11", isoMods: true, sharedMods: false,
            setup: "VersionQuilt:0.26.0\r\nVersionForge:51.0.0\r\nVersionNeoForge:21.0.0\r\n");

        Assert.Equal("neoforge", Assert.Single(ClientScanner.ScanVersions(mc)).Loader);
    }

    [Fact]
    public void ScanVersions_无加载器信息时为vanilla()
    {
        var mc = Path.Combine(_tmp, "vanilla");
        var ver = Path.Combine(mc, "versions", "1.21.11");
        Write(Path.Combine(ver, "1.21.11.json"), """{"id":"1.21.11","libraries":[]}""");

        Assert.Equal("vanilla", Assert.Single(ClientScanner.ScanVersions(mc)).Loader);
    }

    // ------------------------------------------------------------------
    // scanAll
    // ------------------------------------------------------------------

    [Fact]
    public void ScanAll_摊平并带上文件夹信息()
    {
        var a = MakeMc("fa", "1.21.11", isoMods: true, sharedMods: false);
        var b = MakeMc("fb", "1.20.1", isoMods: true, sharedMods: false);

        var all = ClientScanner.ScanAll([
            new GameFolder("甲", a, "log", _tmp, true),
            new GameFolder("乙", b, "log", _tmp, false),
        ]);

        Assert.Equal(2, all.Count);
        Assert.Equal("甲", all[0].FolderName);
        Assert.True(all[0].FolderSelected);
        Assert.False(all[1].FolderSelected);
    }

    // ------------------------------------------------------------------
    // 匹配打分
    // ------------------------------------------------------------------

    private static ClientVersion V(string name, string? game = null, string? loader = null,
        int mods = 0, bool managed = false, bool selected = false, bool folderSelected = false)
        => new()
        {
            Name = name, VersionDir = name, ContentDir = name, Isolated = true, IsolationBasis = "files",
            GameVersion = game, Loader = loader, ModCount = mods, Managed = managed,
            IsSelected = selected, FolderSelected = folderSelected,
        };

    [Fact]
    public void MatchTarget_空列表返回空()
    {
        var (best, ranked) = ClientScanner.MatchTarget([], "1.21.11", "1.21.11", "Fabric");
        Assert.Null(best);
        Assert.Empty(ranked);
    }

    [Fact]
    public void MatchTarget_完全匹配_得分为各条规则之和()
    {
        var (best, ranked) = ClientScanner.MatchTarget(
            [V("1.21.11", "1.21.11", "fabric", mods: 10, managed: true, selected: true, folderSelected: true)],
            "1.21.11", "1.21.11", "Fabric");

        Assert.NotNull(best);
        // 100(版本名) + 30(游戏版本) + 15(加载器) + 25(managed) + 20(选中且文件夹选中) + 5(有 mod)
        Assert.Equal(195, best!.Score);
        Assert.Equal(
            ["版本名完全一致", "同为 1.21.11", "同为 fabric", "之前用本工具更新过", "PCL2 当前正在用"],
            best.Why);
    }

    [Fact]
    public void MatchTarget_仅版本选中而文件夹未选中_只加8分且不写进why()
    {
        var (_, ranked) = ClientScanner.MatchTarget(
            [V("1.21.11", "1.21.11", "fabric", mods: 3, selected: true, folderSelected: false)],
            "1.21.11", "1.21.11", "Fabric");

        // 100(版本名) + 30(游戏版本) + 15(加载器) + 8(仅版本选中) + 5(有 mod)
        Assert.Equal(158, ranked[0].Score);
        Assert.DoesNotContain("PCL2 当前正在用", ranked[0].Why);
    }

    [Fact]
    public void MatchTarget_版本名相近得40分()
    {
        var (_, ranked) = ClientScanner.MatchTarget([V("1.21.11-fancy")], "1.21.11", null, null);
        Assert.Equal(40, ranked[0].Score);
        Assert.Equal(["版本名相近"], ranked[0].Why);
    }

    [Fact]
    public void MatchTarget_低于30分不采纳()
    {
        // 只有「加载器一致」(15) + 「有 mod」(5) = 20 < 30
        var (best, ranked) = ClientScanner.MatchTarget(
            [V("某个别的版本", null, "fabric", mods: 5)], "1.21.11", "1.21.11", "Fabric");

        Assert.Null(best);
        Assert.Single(ranked);
    }

    [Fact]
    public void MatchTarget_同分保持原顺序()
    {
        var (_, ranked) = ClientScanner.MatchTarget(
            [V("first", "1.21.11", "fabric"), V("second", "1.21.11", "fabric")],
            null, "1.21.11", "Fabric");

        // 两个都是 30+15=45；List.Sort 不稳定会随机换序，OrderBy 稳定则保持
        Assert.Equal(["first", "second"], ranked.Select(v => v.Name));
    }

    [Fact]
    public void MatchTarget_整合包元信息为空时不崩()
    {
        var (best, ranked) = ClientScanner.MatchTarget([V("1.21.11", "1.21.11", "fabric")], null, null, null);
        Assert.Null(best);
        Assert.Equal(0, Assert.Single(ranked).Score);
    }

    // ------------------------------------------------------------------
    // describeDir（手动指定）
    // ------------------------------------------------------------------

    [Fact]
    public void DescribeDir_给versions下的版本目录()
    {
        var mc = MakeMc("manual", "1.21.11", isoMods: true, sharedMods: false,
            setup: "VersionFabric:0.19.3\r\nVersionVanillaName:1.21.11\r\n");
        var ver = Path.Combine(mc, "versions", "1.21.11");

        var v = ClientScanner.DescribeDir(ver);

        Assert.True(v.Manual);
        Assert.Equal("1.21.11", v.Name);
        Assert.Equal(2, v.ModCount);
        // 走的是 scanVersions 的返回值，所以 Setup.ini 的信息都在
        Assert.Equal("1.21.11", v.GameVersion);
    }

    [Fact]
    public void DescribeDir_给minecraft目录时取第一个版本()
    {
        var mc = MakeMc("manual2", "1.21.11", isoMods: true, sharedMods: false);

        var v = ClientScanner.DescribeDir(mc);

        Assert.True(v.Manual);
        Assert.Equal("1.21.11", v.Name);
        Assert.Equal(mc, v.FolderPath);
    }

    [Fact]
    public void DescribeDir_裸内容目录()
    {
        var dir = Path.Combine(_tmp, "bare");
        Write(Path.Combine(dir, "mods", "x.jar"));

        var v = ClientScanner.DescribeDir(dir);

        Assert.True(v.Manual);
        Assert.True(v.Isolated);
        Assert.Equal("manual", v.IsolationBasis);
        Assert.Equal(1, v.ModCount);
        Assert.Equal(dir, v.ContentDir);
    }

    [Fact]
    public void DescribeDir_目录不存在抛异常()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => ClientScanner.DescribeDir(Path.Combine(_tmp, "不存在")));
    }

    [Fact]
    public void DescribeDir_不是游戏目录时给出可读的报错()
    {
        var dir = Path.Combine(_tmp, "plain");
        Directory.CreateDirectory(dir);

        var ex = Assert.Throws<InvalidOperationException>(() => ClientScanner.DescribeDir(dir));
        Assert.Contains("没有 mods 文件夹", ex.Message);
    }
}

using System.IO;
using McUpdater.Core;

namespace McUpdater.Tests;

/// <summary>
/// M4：<see cref="Pcl2Detector"/> 的单元测试。
///
/// <para>用临时目录搭出 PCL2 的最小骨架（<c>PCL/Setup.ini</c> + <c>PCL/Log1.txt</c>
/// + 若干 <c>.minecraft</c>），不碰真实目录 —— 真实环境回归在
/// <see cref="RealTargetTests"/> 里做，那份基准由 Node 原版生成。</para>
///
/// <para><b>为什么所有 <c>Detect</c> 调用都显式注入 <c>probe</c></b>：
/// 「PCL2 此刻有没有在跑」是本机瞬时状态，一旦让它参与断言，
/// 测试就会在"开着 PCL2 跑测试"时红。基准也是在未运行时取的，
/// 这里固定注入 <c>(false, null)</c> 复现同一条件。</para>
/// </summary>
public class Pcl2DetectorTests : IDisposable
{
    private readonly string _tmp;

    public Pcl2DetectorTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "mcu-pcl2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* 清理失败无所谓 */ }
        GC.SuppressFinalize(this);
    }

    private static Pcl2ProcessState NotRunning() => new(false, null);

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
    }

    // ------------------------------------------------------------------
    // Setup.ini
    // ------------------------------------------------------------------

    [Fact]
    public void ParseSetupIniFile_读不到时返回空字典而不是抛()
    {
        var r = Pcl2Detector.ParseSetupIniFile(Path.Combine(_tmp, "不存在", "Setup.ini"));
        Assert.Empty(r);
    }

    [Fact]
    public void ParseSetupIniFile_冒号只切第一个_value里可以有冒号()
    {
        var p = Path.Combine(_tmp, "Setup.ini");
        // Logo 是 pack:// 路径、ReleaseTime 带时分秒 —— Node 刻意只切第一个冒号
        WriteFile(p, "Logo:pack://application:,,,/PCL/Images.png\r\nReleaseTime:2025-12-09 20:23\r\n");

        var r = Pcl2Detector.ParseSetupIniFile(p);
        Assert.Equal("pack://application:,,,/PCL/Images.png", r["Logo"]);
        Assert.Equal("2025-12-09 20:23", r["ReleaseTime"]);
    }

    [Fact]
    public void ResolvePcl2Path_美元前缀解析为相对启动器根目录()
    {
        Assert.Equal(Path.Combine("E:/pcl2", "some/.minecraft"),
            Pcl2Detector.ResolvePcl2Path("E:/pcl2", "$some/.minecraft"));
        Assert.Equal("D:/abs/path", Pcl2Detector.ResolvePcl2Path("E:/pcl2", "D:/abs/path"));
        Assert.Null(Pcl2Detector.ResolvePcl2Path("E:/pcl2", null));
    }

    // ------------------------------------------------------------------
    // 根目录识别
    // ------------------------------------------------------------------

    [Fact]
    public void LooksLikePcl2Root_有SetupIni即算()
    {
        var root = Path.Combine(_tmp, "rootA");
        WriteFile(Path.Combine(root, "PCL", "Setup.ini"), "VersionVanillaName:1.21.11\r\n");
        Assert.True(Pcl2Detector.LooksLikePcl2Root(root));
    }

    [Fact]
    public void LooksLikePcl2Root_只有exe也算()
    {
        var root = Path.Combine(_tmp, "rootB");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Plain Craft Launcher 2.exe"), "");
        Assert.True(Pcl2Detector.LooksLikePcl2Root(root));
    }

    [Fact]
    public void LooksLikePcl2Root_空目录或null都不算()
    {
        var root = Path.Combine(_tmp, "rootC");
        Directory.CreateDirectory(root);
        Assert.False(Pcl2Detector.LooksLikePcl2Root(root));
        Assert.False(Pcl2Detector.LooksLikePcl2Root(null));
        Assert.False(Pcl2Detector.LooksLikePcl2Root(""));
    }

    // ------------------------------------------------------------------
    // 日志解析
    // ------------------------------------------------------------------

    [Fact]
    public void ParseFoldersFromLogs_按出现顺序去重且跨文件去重()
    {
        var root = Path.Combine(_tmp, "logs");
        WriteFile(Path.Combine(root, "PCL", "Log1.txt"),
            "14:58:11.418 I [PageSelectLeft] 有效的 Minecraft 文件夹：当前文件夹 > C:\\a\\.minecraft\\\r\n" +
            "14:58:11.420 I [PageSelectLeft] 有效的 Minecraft 文件夹：yks > C:\\b\\.minecraft\r\n");
        WriteFile(Path.Combine(root, "PCL", "Log2.txt"),
            "14:00:00.000 I [PageSelectLeft] 有效的 Minecraft 文件夹：当前文件夹 > C:\\a\\.minecraft\r\n" +
            "14:00:00.001 I [PageSelectLeft] 有效的 Minecraft 文件夹：新 > C:\\new\\.minecraft\r\n");

        var folders = Pcl2Detector.ParseFoldersFromLogs(root);

        // Log2 的第一条与 Log1 重复（只是少了尾斜杠）→ 不重复收录
        Assert.Equal(3, folders.Count);
        Assert.Equal("当前文件夹", folders[0].Name);
        Assert.Equal(@"C:\a\.minecraft", folders[0].Path);
        Assert.Equal("yks", folders[1].Name);
        Assert.Equal("新", folders[2].Name);
        Assert.All(folders, f => Assert.Equal("log", f.Source));
    }

    [Fact]
    public void ParseFoldersFromLogs_没有日志目录返回空()
    {
        Assert.Empty(Pcl2Detector.ParseFoldersFromLogs(Path.Combine(_tmp, "压根不存在")));
    }

    // ------------------------------------------------------------------
    // detect
    // ------------------------------------------------------------------

    /// <summary>搭一个最小 PCL2 根：Setup.ini 指向 selected，日志给出三个文件夹。</summary>
    private string MakeRoot(string name, string selectedAbs)
    {
        var root = Path.Combine(_tmp, name);
        WriteFile(Path.Combine(root, "PCL", "Setup.ini"), $"LaunchFolderSelect:{selectedAbs}\r\n");
        WriteFile(Path.Combine(root, "PCL", "Log1.txt"),
            "00:00:00.000 I [PageSelectLeft] 有效的 Minecraft 文件夹：当前文件夹 > "
            + Path.Combine(root, ".minecraft") + "\r\n"
            + "00:00:00.001 I [PageSelectLeft] 有效的 Minecraft 文件夹：second > "
            + Path.Combine(root, "second", ".minecraft") + "\r\n");
        Directory.CreateDirectory(Path.Combine(root, ".minecraft"));
        Directory.CreateDirectory(Path.Combine(root, "second", ".minecraft"));
        Directory.CreateDirectory(selectedAbs);
        return root;
    }

    [Fact]
    public void Detect_自定义根目录_收录日志文件夹并标出选中项()
    {
        var selected = Path.Combine(_tmp, "sel", ".minecraft");
        var root = MakeRoot("pcl", selected);

        var r = Pcl2Detector.Detect(root, NotRunning);

        Assert.True(r.Found);
        Assert.False(r.Running);
        Assert.NotEmpty(r.Roots);
        Assert.Equal(root, r.Roots[0].Path);
        Assert.Equal("custom", r.Roots[0].Source);

        // 【为什么按 Pcl2Root 过滤而不是直接看总数】
        // 候选里还有本机真实存在的预设根目录（config.js 的 PCL2_DEFAULT_ROOTS），
        // 它会额外贡献游戏文件夹。断言"总共 3 个"会在这台机器上红，
        // 而在没装 PCL2 的机器上绿 —— 这种测试等于埋雷。
        var mine = r.Folders.Where(f => string.Equals(f.Pcl2Root, root, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 顺序：日志里的两条 → Setup.ini 的选中项 → 启动器目录 → 根下扫描
        // （启动器目录即 root/.minecraft，已被日志收录；second 同理 → 都被去重）
        Assert.Equal(3, mine.Count);
        Assert.Equal(Path.Combine(root, ".minecraft"), mine[0].Path);
        Assert.Equal(Path.Combine(root, "second", ".minecraft"), mine[1].Path);
        Assert.Equal(selected, mine[2].Path);
        Assert.Equal("setup", mine[2].Source);

        // 只有 Setup.ini 指向的那个是"选中"
        Assert.Single(mine, f => f.IsSelected);
        Assert.True(mine[2].IsSelected);
    }

    [Fact]
    public void Detect_启动器目录下的minecraft作为兜底线索()
    {
        var root = Path.Combine(_tmp, "bare");
        WriteFile(Path.Combine(root, "PCL", "Setup.ini"), "VersionVanillaName:1.21.11\r\n");
        Directory.CreateDirectory(Path.Combine(root, ".minecraft"));

        var r = Pcl2Detector.Detect(root, NotRunning);

        // 没有日志、没有 LaunchFolderSelect → 只剩「启动器目录」这一条
        var mine = r.Folders.Where(f => string.Equals(f.Pcl2Root, root, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal("启动器目录", Assert.Single(mine).Name);
        Assert.False(mine[0].IsSelected);
    }

    [Fact]
    public void Detect_不存在的目录不会被当成启动器根()
    {
        var bogus = Path.Combine(_tmp, "不是启动器");
        Directory.CreateDirectory(bogus);

        var r = Pcl2Detector.Detect(bogus, NotRunning);

        Assert.DoesNotContain(r.Roots, x => x.Path == bogus);
    }

    [Fact]
    public void Detect_进程在跑时把exe所在目录也纳入候选()
    {
        var root = MakeRoot("running", Path.Combine(_tmp, "sel2", ".minecraft"));
        var exe = Path.Combine(root, "Plain Craft Launcher 2.exe");

        // custom 与 process 指向同一个目录 → 去重后只应出现一次
        var r = Pcl2Detector.Detect(root, () => new Pcl2ProcessState(true, exe));

        Assert.True(r.Running);
        Assert.Single(r.Roots, x => x.Source == "custom");
        Assert.DoesNotContain(r.Roots, x => x.Source == "process");
        Assert.Contains(r.Roots, x => x.IsActive);
    }
}

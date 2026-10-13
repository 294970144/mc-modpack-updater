using System.IO;
using McUpdater.Core;

namespace McUpdater.Tests;

/// <summary>
/// M4 真实环境回归：PCL2 探测 → 版本扫描 → 目标匹配。
///
/// <para><b>样本（只读，绝不写入）</b>：
/// 整合包 <c>D:/Users/zhang/Downloads/1.21.11 1.0.6.zip</c>、
/// PCL2 根目录 <c>E:/mc/pcl2</c>（版本库在 <c>&lt;根&gt;/.minecraft/versions/</c>）。
/// 任一样本不在本机则整组跳过 —— 不能因为换机器就让测试变红。</para>
///
/// <para><b>期望值全部来自 <see cref="RealTargetBaseline"/></b>，
/// 那份文件由 <c>node tools/gen-pcl2-baseline.js</c> 跑 Node 原版实跑产出、
/// 再由 <c>tools/sync-pcl2-baseline.js</c> 誊写。
/// 手抄基准在 M2 已经被证明会错（106 条错 2 处），这里绝不再手抄。</para>
/// </summary>
public class RealTargetTests
{
    private static bool SamplesExist =>
        File.Exists(RealTargetBaseline.PackPath) && Directory.Exists(RealTargetBaseline.Pcl2Root);

    /// <summary>路径统一成分隔符 '/' 再比 —— Windows 与 Node 的正斜杠写法差异不该算差异。</summary>
    private static string Norm(string p) => p.Replace('\\', '/');

    [Fact]
    public void 真实环境_探测到的文件夹与基准一致()
    {
        if (!SamplesExist) return;

        // 【固定注入"PCL2 未运行"】基准是在未运行时取的。
        // 进程状态是本机瞬时值，让它参与断言会让测试在开着 PCL2 时随机变红。
        var r = Pcl2Detector.Detect(
            RealTargetBaseline.Pcl2Root,
            static () => new Pcl2ProcessState(false, null));

        Assert.Equal(RealTargetBaseline.DetectFound, r.Found);
        Assert.Equal(RealTargetBaseline.FolderCount, r.Folders.Count);

        Assert.Equal(RealTargetBaseline.Folders.Length, r.Folders.Count);
        for (var i = 0; i < r.Folders.Count; i++)
        {
            var expect = RealTargetBaseline.Folders[i];
            Assert.Equal(expect.Name, r.Folders[i].Name);
            Assert.Equal(Norm(expect.Path), Norm(r.Folders[i].Path));
            Assert.Equal(expect.Source, r.Folders[i].Source);
            Assert.Equal(expect.IsSelected, r.Folders[i].IsSelected);
        }
    }

    [Fact]
    public void 真实环境_扫描到的版本与基准一致()
    {
        if (!SamplesExist) return;

        var r = Pcl2Detector.Detect(
            RealTargetBaseline.Pcl2Root,
            static () => new Pcl2ProcessState(false, null));
        var all = ClientScanner.ScanAll(r.Folders);

        // 【为什么按 contentDir 配对而不是按下标】
        // modCount 是玩家自己会改的东西（装/删 mod），而扫描排序里
        // 有一级就是 modCount 降序 —— 按下标比对会在"Master 玩了一局游戏"
        // 之后误报，看不出到底是我的代码错了还是他的游戏变了。
        // contentDir 是版本身份，用它配对；顺序规则单独断言。
        var expected = RealTargetBaseline.Versions
            .ToDictionary(v => Norm(v.ContentDir), StringComparer.Ordinal);

        Assert.Equal(expected.Count, all.Count);
        foreach (var v in all)
        {
            Assert.True(expected.TryGetValue(Norm(v.ContentDir), out var e),
                $"扫到了基准里没有的版本：{v.ContentDir}");
            AssertField(e!, v);
        }

        // 排序规则只在**同一个游戏文件夹内**生效。
        //
        // 【保真细节，我第一版就写错了】Node 的 scanAll 是
        // `for (const f of folders) { for (const v of scanVersions(f.path)) ... }`
        // —— 逐个文件夹拼接，<em>没有</em>再做一次全局排序。
        // 所以跨文件夹之间无从比较，把规则当成全局断言必然误报。
        foreach (var group in all.GroupBy(v => v.FolderPath, StringComparer.OrdinalIgnoreCase))
        {
            var list = group.ToList();
            for (var i = 1; i < list.Count; i++)
            {
                var a = list[i - 1];
                var b = list[i];
                Assert.False(!a.IsSelected && b.IsSelected,
                    $"未选中的版本排在了选中的前面：{a.Name} → {b.Name}");
                Assert.False(a.IsSelected == b.IsSelected && !a.Managed && b.Managed,
                    $"非受管版本排在了受管的前面：{a.Name} → {b.Name}");
                Assert.False(a.IsSelected == b.IsSelected && a.Managed == b.Managed && a.ModCount < b.ModCount,
                    $"mod 少的版本排在了多的前面：{a.Name} → {b.Name}");
            }
        }
    }

    private static void AssertField(RealVersion e, ClientVersion v)
    {
        Assert.Equal(e.Name, v.Name);
        Assert.Equal(e.FolderName, v.FolderName);
        Assert.Equal(Norm(e.FolderPath), Norm(v.FolderPath!));
        Assert.Equal(Norm(e.ContentDir), Norm(v.ContentDir));
        Assert.Equal(e.Isolated, v.Isolated);
        Assert.Equal(e.IsolationBasis, v.IsolationBasis);
        Assert.Equal(e.GameVersion, v.GameVersion);
        Assert.Equal(e.Loader, v.Loader);
        Assert.Equal(e.LoaderVersion, v.LoaderVersion);
        Assert.Equal(e.IsSelected, v.IsSelected);
        Assert.Equal(e.FolderSelected, v.FolderSelected);
        Assert.Equal(e.Managed, v.Managed);
        // modCount 只做合理性检查，不做等值 —— 它是玩家随时会改的量，
        // 精确计数由合成目录的单元测试（ClientScannerTests）覆盖。
        Assert.True(v.ModCount > 0, $"{v.Name} 的 mod 数为 0，计数逻辑可能坏了");
    }

    [Fact]
    public void 真实环境_自动匹配结果与基准一致()
    {
        if (!SamplesExist) return;

        using var handle = ModpackReader.Open(RealTargetBaseline.PackPath, (_, _) => { });

        // 先确认「Node 的 pack.versionName」对应到 C# 的哪个属性 ——
        // 这条对不上，后面所有分数都会偏，而分数偏了界面上完全看不出原因。
        Assert.Equal(RealTargetBaseline.VersionName, handle.VersionNameHint);
        Assert.Equal(RealTargetBaseline.GameVersion, handle.GameVersion);
        Assert.Equal(RealTargetBaseline.Loader, handle.Loader);

        var r = Pcl2Detector.Detect(
            RealTargetBaseline.Pcl2Root,
            static () => new Pcl2ProcessState(false, null));
        var all = ClientScanner.ScanAll(r.Folders);
        var (best, ranked) = ClientScanner.MatchTarget(
            all, handle.VersionNameHint, handle.GameVersion, handle.Loader);

        Assert.Equal(RealTargetBaseline.Ranked.Length, ranked.Count);
        for (var i = 0; i < ranked.Count; i++)
        {
            var e = RealTargetBaseline.Ranked[i];
            Assert.Equal(e.Name, ranked[i].Name);
            Assert.Equal(e.FolderName, ranked[i].FolderName);
            Assert.Equal(Norm(e.ContentDir), Norm(ranked[i].ContentDir));
            Assert.Equal(e.Score, ranked[i].Score);
            Assert.Equal(e.Why, ranked[i].Why);
        }

        var expectBest = RealTargetBaseline.Best;
        Assert.NotNull(expectBest);
        Assert.NotNull(best);
        Assert.Equal(expectBest!.Name, best!.Name);
        Assert.Equal(expectBest.FolderName, best.FolderName);
        Assert.Equal(Norm(expectBest.ContentDir), Norm(best.ContentDir));
        Assert.Equal(expectBest.Score, best.Score);
        Assert.Equal(expectBest.Why, best.Why);
    }
}

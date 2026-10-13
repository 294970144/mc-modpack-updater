using McUpdater.Core;

namespace McUpdater.Tests;

/// <summary>
/// M5：<see cref="DiffPlanner"/> 的单元测试。
///
/// <para>完整链路（要开真实整合包）的回归在 <see cref="RealDiffPlanTests"/>，
/// 那份基准由 Node 原版 <c>runDiff</c> 实跑得出。这里只测不需要真实样本的
/// 编排规则：冲突合并与空范围保护。</para>
/// </summary>
public class DiffPlannerTests
{
    private static DiffConflict Conflict(string side, string key, params string[] files)
        => new()
        {
            Side = side,
            Key = key,
            DisplayName = key,
            Files = files.Select(f => new DiffConflictFile(f, "1.0")).ToList(),
        };

    [Fact]
    public void MergeConflicts_单条原样返回()
    {
        var r = DiffPlanner.MergeConflicts([Conflict("local", "id:a", "a.jar")]);
        Assert.Single(r);
        Assert.Equal("local", r[0].Side);
    }

    [Fact]
    public void MergeConflicts_两侧文件相同则合成一条且标both()
    {
        // 玩家的 mods 本来就是上次从整合包装的，所以同一个重复会两侧各报一次。
        // 文件列表一样 → 合成一条，并说清根因在整合包。
        var r = DiffPlanner.MergeConflicts([
            Conflict("zip", "id:a", "a-1.0.jar", "a-1.1.jar"),
            Conflict("local", "id:a", "a-1.1.jar", "a-1.0.jar"),   // 顺序不同也要算同
        ]);

        Assert.Single(r);
        Assert.Equal("both", r[0].Side);
        Assert.Equal(2, r[0].Files.Count);
    }

    [Fact]
    public void MergeConflicts_两侧文件不同则是两个独立问题()
    {
        var r = DiffPlanner.MergeConflicts([
            Conflict("zip", "id:a", "a-1.0.jar"),
            Conflict("local", "id:a", "a-2.0.jar"),
        ]);

        Assert.Equal(2, r.Count);
        Assert.Equal(["zip", "local"], r.Select(c => c.Side));
    }

    [Fact]
    public void MergeConflicts_只有本地侧时side保持local()
    {
        var r = DiffPlanner.MergeConflicts([
            Conflict("local", "id:a", "a.jar", "a-copy.jar"),
            Conflict("local", "id:a", "a.jar", "a-copy.jar"),
        ]);

        Assert.Single(r);
        Assert.Equal("local", r[0].Side);
    }

    [Fact]
    public void MergeConflicts_不同key互不干扰()
    {
        var r = DiffPlanner.MergeConflicts([
            Conflict("zip", "id:a", "a.jar"),
            Conflict("local", "id:b", "b.jar"),
        ]);
        Assert.Equal(2, r.Count);
    }

    [Fact]
    public void MergeConflicts_空列表返回空()
    {
        Assert.Empty(DiffPlanner.MergeConflicts([]));
    }

    [Fact]
    public void Run_空范围直接抛而不是静默返回空清单()
    {
        // 一个分类都不勾时跑对比，返回"完全一致"会让用户以为真的没差异 ——
        // 必须明确报错。
        Assert.Throws<InvalidOperationException>(
            () => DiffPlanner.Run(null!, "x", []));
    }
}

using System.IO;
using McUpdater.Core;

namespace McUpdater.Tests;

/// <summary>
/// M5 真实环境回归：完整对比链路（四个分类全开）。
///
/// <para><b>样本（只读，绝不写入）</b>：
/// 整合包 <c>D:/Users/zhang/Downloads/1.21.11 1.0.6.zip</c> ×
/// 目标 <c>E:/mc/mc26/Client1.21.11/.minecraft/versions/1.21.11</c>
/// （M4 自动匹配到的那个，195 分）。样本不在本机则整组跳过。</para>
///
/// <para><b>期望值全部来自 <see cref="RealDiffPlanBaseline"/></b>，
/// 由 <c>node tools/gen-diffplan-baseline.js</c> 跑 Node 原版
/// <c>runDiff</c> 产出、<c>tools/sync-diffplan-baseline.js</c> 誊写。</para>
///
/// <para><b>不落 CRC 缓存是刻意的</b>：真实样本只读，
/// <see cref="DiffPlanner"/> 因此只用内存态 <see cref="MetaCache"/>。</para>
/// </summary>
public class RealDiffPlanTests
{
    /// <summary>
    /// 目标目录里当前的 jar 数。与基准记录的不一致就说明玩家动过游戏目录，
    /// 整套对比结果必然漂移 —— 这时跳过而不是报红：
    /// 「我的代码错了」和「Master 装了几个 mod」必须能分开，否则这套
    /// 真实回归就变成了一个每隔几天就诈胡一次的噪音源。
    /// </summary>
    private static bool EnvUnchanged =>
        Directory.Exists(Path.Combine(RealDiffPlanBaseline.TargetDir, "mods"))
        && Directory.EnumerateFiles(Path.Combine(RealDiffPlanBaseline.TargetDir, "mods"))
               .Count(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
           == RealDiffPlanBaseline.TargetJarCount;

    private static bool SamplesExist =>
        RealDiffPlanBaseline.Available
        && File.Exists(RealDiffPlanBaseline.PackPath)
        && Directory.Exists(RealDiffPlanBaseline.TargetDir)
        && EnvUnchanged;

    [Fact]
    public void 真实环境_对比结果与Node基准一致()
    {
        if (!SamplesExist) return;

        using var handle = ModpackReader.Open(RealDiffPlanBaseline.PackPath, (_, _) => { });

        var plan = DiffPlanner.Run(handle, RealDiffPlanBaseline.TargetDir,
            RealDiffPlanBaseline.Categories, (_, _) => { });

        // 分类顺序必须是配置顺序，不是传入顺序（这里传入顺序恰好一致也要验）
        Assert.Equal(RealDiffPlanBaseline.Categories, plan.Categories);

        // ---- 条数与分类型统计 ----
        Assert.Equal(RealDiffPlanBaseline.Total, plan.Changes.Count);
        Assert.Equal(RealDiffPlanBaseline.Added, plan.Summary.Added);
        Assert.Equal(RealDiffPlanBaseline.Updated, plan.Summary.Updated);
        Assert.Equal(RealDiffPlanBaseline.Removed, plan.Summary.Removed);
        Assert.Equal(RealDiffPlanBaseline.Unchanged, plan.Summary.Unchanged);
        Assert.Equal(RealDiffPlanBaseline.Changed, plan.Summary.Changed);

        // ---- 写入量：只算勾选中的非无变化项 ----
        Assert.Equal(RealDiffPlanBaseline.Bytes, plan.Summary.Bytes);
        Assert.Equal(RealDiffPlanBaseline.BytesText, plan.Summary.BytesText);

        // ---- 归属判断 ----
        Assert.Equal(RealDiffPlanBaseline.PlayerInstalledRemovals, plan.Summary.PlayerInstalledRemovals);
        Assert.Equal(RealDiffPlanBaseline.FirstRun, plan.Summary.FirstRun);

        // ---- 被跳过的文件 ----
        Assert.Equal(RealDiffPlanBaseline.SkippedProtected, plan.Summary.Skipped.Protected);
        Assert.Equal(RealDiffPlanBaseline.SkippedOversize, plan.Summary.Skipped.Oversize);
        Assert.Equal(RealDiffPlanBaseline.SkippedSamples, plan.Summary.Skipped.Samples);

        // ---- 冲突（Node 侧会再做一次 mergeConflicts，本机样本为 0）----
        // 用 Assert.True 而不是 Assert.Equal：后者在期望值为 0 时会被
        // xUnit 分析器（xUnit2013）当成"该用 Assert.Empty"而告警。
        Assert.True(plan.Conflicts.Count == RealDiffPlanBaseline.Conflicts,
            $"冲突数 {plan.Conflicts.Count} != 基准 {RealDiffPlanBaseline.Conflicts}");
    }

    [Fact]
    public void 真实环境_变更顺序与Node基准一致()
    {
        if (!SamplesExist) return;

        using var handle = ModpackReader.Open(RealDiffPlanBaseline.PackPath, (_, _) => { });

        var plan = DiffPlanner.Run(handle, RealDiffPlanBaseline.TargetDir,
            RealDiffPlanBaseline.Categories, (_, _) => { });

        // 排序规则：需要注意的排前面 → 分类名 → 展示名（中文区域）
        var actual = plan.Changes
            .Select(c => $"{c.Type}|{c.Category}|{c.Key}|{c.FileName}|{(c.Checked ? 1 : 0)}")
            .ToList();

        Assert.Equal(RealDiffPlanBaseline.Head.Length,
            Math.Min(RealDiffPlanBaseline.Head.Length, actual.Count));
        for (var i = 0; i < RealDiffPlanBaseline.Head.Length; i++)
        {
            Assert.Equal(RealDiffPlanBaseline.Head[i], actual[i]);
        }
    }

    [Fact]
    public void 真实环境_推荐勾选与展示层自洽()
    {
        if (!SamplesExist) return;

        using var handle = ModpackReader.Open(RealDiffPlanBaseline.PackPath, (_, _) => { });

        var plan = DiffPlanner.Run(handle, RealDiffPlanBaseline.TargetDir,
            RealDiffPlanBaseline.Categories, (_, _) => { });

        var recommended = ChangeList.RecommendedKeys(plan.Changes);
        var rows = ChangeList.BuildRows(plan.Changes);

        // 1) 无变化项绝不能进推荐勾选（应用阶段会白写一遍）
        Assert.DoesNotContain(rows.Where(r => !r.CanCheck).Select(r => r.SelKey), k => recommended.Contains(k));

        // 2) 展示层的统计必须与勾选集合一致
        Assert.Equal(recommended.Count, ChangeList.Status(plan.Changes, recommended).Count);

        // 3) 分组不能漏项
        var visible = ChangeList.Visible(rows, "all", null);
        Assert.Equal(visible.Count, ChangeList.Group(visible).Sum(g => g.Rows.Count));

        // 4) 「全部」视图里不该出现无变化项
        Assert.DoesNotContain(visible, r => r.Item.Type == ChangeType.Unchanged);
    }
}

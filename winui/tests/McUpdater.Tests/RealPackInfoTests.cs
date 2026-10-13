using System;
using System.IO;
using System.Linq;
using McUpdater.Core;
using Xunit;
using Xunit.Abstractions;

namespace McUpdater.Tests;

/// <summary>
/// M3 读包链路的真实样本回归。
///
/// <para><b>样本</b>：<c>D:/Users/zhang/Downloads/1.21.11 1.0.6.zip</c>
/// （226MB，PCL2 导出的 mrpack 本体）。<b>只读访问，绝不写入或修改。</b>
/// 文件不存在时整组测试跳过 —— 不能因为Master 换机器就让 CI 变红。</para>
///
/// <para><b>这组测试验的是「UI 侧看到的数字对不对」</b>，不是 diff 逻辑
/// （diff 由 <see cref="RealDiffTests"/> 覆盖）。M3 的风险恰恰在展示层：
/// 包名取错、分类计数错、loader 漏读 —— 这些都不会让diff 变错，
/// 但会让用户拿到一份信息错误的界面。</para>
/// </summary>
public class RealPackInfoTests
{
    private const string RealPack = @"D:\Users\zhang\Downloads\1.21.11 1.0.6.zip";

    private readonly ITestOutputHelper _out;

    public RealPackInfoTests(ITestOutputHelper output) => _out = output;

    /// <summary>样本不存在就跳过，而不是失败。</summary>
    private static bool PackExists => File.Exists(RealPack);

    [Fact]
    public void 真实包_格式与元信息()
    {
        if (!PackExists) return;   // 样本不在本机则放过（沿用 RealPackTests 的约定）

        using var handle = ModpackReader.Open(RealPack, (msg, pct) => { });

        // 上一轮 M2 已确认：这是 mrpack 本体（不是 PCL2 双层）
        Assert.Equal(ModpackFormat.Mrpack, handle.Format);
        // 内容根是 overrides
        Assert.Equal(AppConfig.MrpackOverrides, handle.ContentRoot);

        // M2 实测基准：Fabric 0.19.3 / MC 1.21.11
        Assert.Equal("Fabric", handle.Loader);
        Assert.Equal("0.19.3", handle.LoaderVersion);
        Assert.Equal("1.21.11", handle.GameVersion);

        _out.WriteLine($"包名：{handle.PackName}");
        _out.WriteLine($"版本：{handle.PackVersion}");
        _out.WriteLine($"隔离：{handle.IsolationHint}");
    }

    [Fact]
    public void 真实包_分类计数与M2基准一致()
    {
        if (!PackExists) return;   // 样本不在本机则放过（沿用 RealPackTests 的约定）

        using var handle = ModpackReader.Open(RealPack, (msg, pct) => { });

        // M2 基准数据（由 Node 原版实跑得出）
        Assert.Equal(90, Count(handle, "mods"));
        Assert.Equal(191, Count(handle, "config"));
        Assert.Equal(8, Count(handle, "resourcepacks"));
        Assert.Equal(3, Count(handle, "shaderpacks"));
    }

    [Fact]
    public void 真实包_进度回调按序且单调不减()
    {
        if (!PackExists) return;   // 样本不在本机则放过（沿用 RealPackTests 的约定）

        var frames = new System.Collections.Generic.List<(string Msg, int Pct)>();
        using var handle = ModpackReader.Open(RealPack, (msg, pct) => frames.Add((msg, pct)));

        Assert.NotEmpty(frames);

        // 【保真】最后一条是 35，不是 100。
        // Node 的 openModpack 最后一次 report 是 ('整合包读取完成', 35)——
        // 这套百分比是「读包阶段」的刻度（5→15→25→30→35），
        // 100% 由 UI 层在拿到结果后自己补（app.js: overlay.progress('完成', 100)）。
        // 我最初的测试写成"末帧必须是 100"，等于把 UI 的职责错记到 Core 头上。
        Assert.Equal(35, frames[^1].Pct);
        Assert.Contains(frames, f => f.Msg.Contains("完成"));

        // 起点是 5
        Assert.Equal(5, frames[0].Pct);

        // 单调不减：进度条倒着走是明显的用户可见 bug
        for (var i = 1; i < frames.Count; i++)
        {
            Assert.True(frames[i].Pct >= frames[i - 1].Pct,
                $"进度倒退了：第 {i - 1} 帧 {frames[i - 1].Pct} → 第 {i} 帧 {frames[i].Pct}");
        }

        // 每帧都要有文案（UI 直接显示它，空文案会让遮罩看起来卡死）
        Assert.All(frames, f => Assert.False(string.IsNullOrWhiteSpace(f.Msg)));

        _out.WriteLine($"共 {frames.Count} 帧：");
        foreach (var (msg, pct) in frames) _out.WriteLine($"  {pct,3}%  {msg}");
    }

    [Fact]
    public void 真实包_重复打开有缓存不应出错()
    {
        if (!PackExists) return;   // 样本不在本机则放过（沿用 RealPackTests 的约定）

        // 连续开两次：验证 Dispose / 缓存路径不会互相干扰
        using (var h1 = ModpackReader.Open(RealPack, (m, p) => { }))
        {
            Assert.Equal(90, Count(h1, "mods"));
        }
        using (var h2 = ModpackReader.Open(RealPack, (m, p) => { }))
        {
            Assert.Equal(90, Count(h2, "mods"));
        }
    }

    private static int Count(ModpackHandle h, string key)
        => h.CategoryCounts.TryGetValue(key, out var n) ? n : 0;
}
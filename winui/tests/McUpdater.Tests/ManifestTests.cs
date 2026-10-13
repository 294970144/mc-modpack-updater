using System.Text;
using McUpdater.Core;
using Xunit;

namespace McUpdater.Core.Tests;

/// <summary>
/// <c>Manifest</c> 只读侧测试。
///
/// <para>manifest 的存在意义是区分「整合包装的」与「玩家自己装的」，
/// 所以读取侧最关键的三条语义是：<b>不存在/损坏一律返回 null</b>（当首次运行）、
/// <b>mods 缺失要归一成空列表</b>（下游 <c>Count &gt; 0</c> 依赖它）、
/// <b>空串字段归一成 null</b>（否则空 modId 会误判归属）。</para>
/// </summary>
public class ManifestTests
{
    /// <summary>
    /// 建一个临时目标目录。
    /// <paramref name="manifestJson"/> 传 null 表示"不放安装记录文件" ——
    /// 参数类型必须是 <c>string?</c>，否则「文件不存在」这条用例只能靠
    /// <c>null!</c> 糊过去（原本就是这么写的，编译器警告 CS8625 指的正是它）。
    /// </summary>
    private static string MakeTarget(string? manifestJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mcmani-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        if (manifestJson is not null)
        {
            File.WriteAllText(Path.Combine(dir, AppConfig.ManifestName), manifestJson);
        }
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
    }

    // ==================================================================
    // 路径
    // ==================================================================

    [Fact]
    public void ManifestPath_落在目标目录下_文件名固定()
    {
        var p = Manifest.ManifestPath(@"D:\mc\versions\1.21.11");
        Assert.Equal(Path.Combine(@"D:\mc\versions\1.21.11", ".mcupdater-manifest.json"), p);
    }

    // ==================================================================
    // 缺失 / 损坏
    // ==================================================================

    [Fact]
    public void Load_文件不存在_返回null()
    {
        var dir = MakeTarget(null);
        try { Assert.Null(Manifest.Load(dir)); }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_目录不存在_返回null()
        => Assert.Null(Manifest.Load(Path.Combine(Path.GetTempPath(), "不存在的目标目录-xyz")));

    [Fact]
    public void Load_JSON损坏_返回null而非抛异常()
    {
        var dir = MakeTarget("{ 这不是合法 JSON");
        try { Assert.Null(Manifest.Load(dir)); }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_顶层是数组_返回null()
    {
        // 【刻意收紧】JS 里 typeof [] === 'object' 会通过检查，
        // 但 manifest 顶层按设计是对象，数组根一定是坏文件 → 当首次运行处理
        var dir = MakeTarget("[1, 2, 3]");
        try { Assert.Null(Manifest.Load(dir)); }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_顶层是字符串_返回null()
    {
        var dir = MakeTarget("\"just a string\"");
        try { Assert.Null(Manifest.Load(dir)); }
        finally { Cleanup(dir); }
    }

    // ==================================================================
    // 归一化
    // ==================================================================

    [Fact]
    public void Load_空对象_归一成空mods与空files()
    {
        var dir = MakeTarget("{}");
        try
        {
            var m = Manifest.Load(dir);
            Assert.NotNull(m);
            Assert.Empty(m!.Mods);          // 下游 mods.Count > 0 判断依赖这个
            Assert.Empty(m.Files);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_mods字段类型错误_归一成空列表()
    {
        var dir = MakeTarget("""{ "mods": "不是数组" }""");
        try
        {
            var m = Manifest.Load(dir);
            Assert.NotNull(m);
            Assert.Empty(m!.Mods);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_files字段类型错误_归一成空字典()
    {
        var dir = MakeTarget("""{ "files": ["不是对象"] }""");
        try
        {
            var m = Manifest.Load(dir);
            Assert.NotNull(m);
            Assert.Empty(m!.Files);
        }
        finally { Cleanup(dir); }
    }

    // ==================================================================
    // 正常读取
    // ==================================================================

    [Fact]
    public void Load_完整manifest_读出全部字段()
    {
        var dir = MakeTarget("""
        {
          "version": 1,
          "tool": "我的世界更新助手",
          "targetDir": "D:/mc/versions/1.21.11",
          "packName": "测试整合包",
          "packVersion": "1.0.6",
          "gameVersion": "1.21.11",
          "loader": "Fabric",
          "loaderVersion": "0.16.10",
          "appliedAt": "2026-10-09T12:00:00.000Z",
          "mods": [
            { "id": "sodium", "fileName": "sodium.jar", "version": "0.8.13" },
            { "id": "lithium", "fileName": "lithium-fabric.jar", "version": "0.2.0", "playerInstalled": true }
          ],
          "files": {
            "config": [ { "path": "a.toml" }, { "path": "sub/b.toml", "playerInstalled": true } ],
            "shaderpacks": []
          },
          "history": [
            { "at": "2026-10-08T12:00:00.000Z", "packName": "旧包", "added": 3 }
          ]
        }
        """);
        try
        {
            var m = Manifest.Load(dir);
            Assert.NotNull(m);
            Assert.Equal("测试整合包", m!.PackName);
            Assert.Equal("1.0.6", m.PackVersion);
            Assert.Equal("1.21.11", m.GameVersion);
            Assert.Equal("Fabric", m.Loader);
            Assert.Equal("0.16.10", m.LoaderVersion);
            Assert.Equal("D:/mc/versions/1.21.11", m.TargetDir);
            Assert.Equal("2026-10-09T12:00:00.000Z", m.AppliedAt);

            Assert.Equal(2, m.Mods.Count);
            Assert.Equal("sodium", m.Mods[0].Id);
            Assert.Equal("0.8.13", m.Mods[0].Version);
            Assert.Null(m.Mods[0].PlayerInstalled);
            Assert.True(m.Mods[1].PlayerInstalled);

            Assert.Equal(2, m.Files.Count);
            Assert.Equal(2, m.Files["config"].Count);
            Assert.Equal("sub/b.toml", m.Files["config"][1].Path);
            Assert.True(m.Files["config"][1].PlayerInstalled);
            Assert.Empty(m.Files["shaderpacks"]);

            Assert.Single(m.History);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_空字符串字段_归一为null()
    {
        // 【关键】空 modId 若原样留在集合里，会把某个解析不出 id 的 mod
        // 误判成"整合包装的"从而默认勾选删除
        var dir = MakeTarget("""
        {
          "mods": [ { "id": "", "fileName": "x.jar" } ],
          "files": { "config": [ { "path": "" } ] }
        }
        """);
        try
        {
            var m = Manifest.Load(dir);
            Assert.NotNull(m);
            Assert.Null(m!.Mods[0].Id);
            Assert.Equal("x.jar", m.Mods[0].FileName);
            // path 同样归一成 null，但下游 diffFiles 用的是
            // `String(f.path || '')`（Node 第 520 行），null 在那里仍得到 ""，
            // 最终行为与 Node 一致 —— 空 path 只是匹配不到任何真实文件
            Assert.Null(m.Files["config"][0].Path);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_mods数组含非对象元素_跳过坏元素()
    {
        var dir = MakeTarget("""
        { "mods": [ "字符串", 123, { "id": "ok" } ] }
        """);
        try
        {
            var m = Manifest.Load(dir);
            Assert.NotNull(m);
            var mod = Assert.Single(m!.Mods);
            Assert.Equal("ok", mod.Id);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_字段类型不符_忽略而非崩溃()
    {
        var dir = MakeTarget("""
        {
          "packName": 123,
          "mods": [ { "id": "a", "playerInstalled": "yes" } ],
          "files": { "config": "不是数组" }
        }
        """);
        try
        {
            var m = Manifest.Load(dir);
            Assert.NotNull(m);
            Assert.Null(m!.PackName);                    // 数字不是字符串 → null
            Assert.Null(m.Mods[0].PlayerInstalled);       // 字符串不是布尔 → null
            Assert.Empty(m.Files["config"]);               // 类型错 → 空列表
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_分类名大小写_可按任意大小写查出()
    {
        // Files 用 OrdinalIgnoreCase：diff 侧用 categoryKey 直接索引，
        // 而键来自 manifest 写入时的原样分类名，容忍大小写更稳
        var dir = MakeTarget("""{ "files": { "Config": [ { "path": "a.toml" } ] } }""");
        try
        {
            var m = Manifest.Load(dir);
            Assert.NotNull(m);
            Assert.Single(m!.Files["config"]);
            Assert.Single(m.Files["CONFIG"]);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_重复读取_结果一致()
    {
        var dir = MakeTarget("""{ "mods": [ { "id": "a" } ], "packName": "p" }""");
        try
        {
            var a = Manifest.Load(dir);
            var b = Manifest.Load(dir);
            Assert.Equal(a!.PackName, b!.PackName);
            Assert.Single(b.Mods);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Load_带BOM的文件_能正常解析()
    {
        // .NET 的 File.WriteAllText 默认不写 BOM，但别的工具可能写；
        // 确认整条读取路径都能正确剥掉 BOM
        var dir = Path.Combine(Path.GetTempPath(), $"mcmani-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var withBom = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("""{ "packName": "bom" }""")).ToArray();
        File.WriteAllBytes(Path.Combine(dir, AppConfig.ManifestName), withBom);
        try
        {
            var m = Manifest.Load(dir);
            Assert.NotNull(m);
            Assert.Equal("bom", m!.PackName);
        }
        finally { Cleanup(dir); }
    }

    // ==================================================================
    // 与 DiffEngine 的衔接（归属判断）
    // ==================================================================

    [Fact]
    public void Load_供diff判断玩家自装_空modId不会误判()
    {
        var dir = MakeTarget("""
        { "mods": [ { "id": "", "fileName": "real.jar" } ] }
        """);
        try
        {
            var m = Manifest.Load(dir);
            Assert.NotNull(m);
            // 下游用 manifestIds.Contains(id) 判定"整合包装的"。
            // 空 id 被归一成 null 后不会进集合，那个 mod 会被正确判为玩家自装。
            var ids = m!.Mods.Where(x => !string.IsNullOrEmpty(x.Id)).Select(x => x.Id).ToList();
            Assert.Empty(ids);
        }
        finally { Cleanup(dir); }
    }
}
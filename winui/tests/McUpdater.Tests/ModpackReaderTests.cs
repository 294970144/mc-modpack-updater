using McUpdater.Core;
using Xunit;

namespace McUpdater.Core.Tests;

/// <summary>
/// <c>ModpackReader</c> 测试。
///
/// <para><b>期望值全部来自 Node 原版实跑</b>（<c>tools/gen-baseline.js</c> 与
/// <c>gen-baseline2.js</c>），不是手算。这样能保证 C# 侧连反直觉的行为都与原版
/// 一致 —— 比如 <c>compareVersion("1.0.0+build.7", "1.0.0")</c> 原版返回
/// <b>1</b>（不是 0），<c>parseSetupIni</c> 会保留空字符串键。</para>
/// </summary>
public class ModpackReaderTests
{
    // ==================================================================
    // parseSetupIni —— 基准来自 Node 实跑
    // ==================================================================

    [Fact]
    public void ParseSetupIni_正常ini_解析出全部键值()
    {
        var r = ModpackReader.ParseSetupIni(
            "; 注释行\r\n# 井号注释\r\n\r\nVersionVanillaName: 1.21.11\r\n  VersionFabric : 0.16.10 \r\nVersionArgumentIndie: 1\r\n没有冒号的行\r\n: 只有冒号\r\n");

        Assert.Equal("1.21.11", r["VersionVanillaName"]);
        Assert.Equal("0.16.10", r["VersionFabric"]);
        Assert.Equal("1", r["VersionArgumentIndie"]);

        // 【保真】Node 不丢弃空键名，`": 只有冒号"` 这行会存成 out[""]
        Assert.Equal("只有冒号", r[""]);

        // 无冒号的行不入表
        Assert.DoesNotContain("没有冒号的行", r.Keys);

        // 键值两侧空白都trim 掉
        Assert.Equal("0.16.10", r["VersionFabric"]);
    }

    [Fact]
    public void ParseSetupIni_空串_返回空字典()
    {
        Assert.Empty(ModpackReader.ParseSetupIni(""));
    }

    [Fact]
    public void ParseSetupIni_裸CR不视为换行_与原版一致()
    {
        // Node 只 replace(/\r\n/) 不处理裸 CR，所以整行是一个键
        var r = ModpackReader.ParseSetupIni("裸CR\r行:值\r");

        Assert.Single(r);
        Assert.Equal("值", r["裸CR\r行"]);
    }

    [Fact]
    public void ParseSetupIni_键名大小写敏感_与JS对象语义一致()
    {
        // JS 对象键区分大小写：小写键不应被当成 VersionFabric
        var r = ModpackReader.ParseSetupIni("versionfabric: 0.16.10\nVersionFabric: 0.16.9");
        Assert.Equal("0.16.9", r["VersionFabric"]);
        Assert.Equal("0.16.10", r["versionfabric"]);
    }

    // ==================================================================
    // detectContentRoots —— 基准来自 Node 实跑
    // ==================================================================

    private static List<ZipEntry> FakeEntries(params string[] names)
        => [.. names.Select(n => new ZipEntry
        {
            Name = n, IsDir = false, Method = 0, Crc32 = 0, CompSize = 0, Size = 1, LocalOffset = 0,
        })];

    [Fact]
    public void DetectContentRoots_真实PCL2结构_优先选中overrides()
    {
        // Node 实跑结果：overrides 以119 分排第一，远高于其它候选
        var r = ModpackReader.DetectContentRoots(FakeEntries(
            "overrides/mods/a.jar", "overrides/config/b.toml", "overrides/shaderpacks/c.zip",
            ".minecraft/versions/1.21.11/mods/x.jar",
            ".minecraft/versions/1.21.11/config/y.toml",
            "versions/1.21.11/mods/z.jar",
            "nested/overrides/mods/n.jar",
            "weird/shaderpacks/s.zip",
            "mods/top.jar",
            "config/top.toml",
            "a/b/c/mods/deep.jar",
            "a/b/mods/mid.jar",
            "lonely.jar",
            "a/b/c/d.txt",
            "overrides/config/sub/config/inner.toml"));

        Assert.Equal("overrides", r[0].Root);
        Assert.Equal(119, r[0].Score);
        Assert.Equal(["mods", "config", "shaderpacks"], r[0].Categories);
    }

    [Fact]
    public void DetectContentRoots_完整排序与评分_逐项对齐Node基准()
    {
        var r = ModpackReader.DetectContentRoots(FakeEntries(
            "overrides/mods/a.jar", "overrides/config/b.toml", "overrides/shaderpacks/c.zip",
            ".minecraft/versions/1.21.11/mods/x.jar",
            ".minecraft/versions/1.21.11/config/y.toml",
            "versions/1.21.11/mods/z.jar",
            "nested/overrides/mods/n.jar",
            "weird/shaderpacks/s.zip",
            "mods/top.jar",
            "config/top.toml",
            "a/b/c/mods/deep.jar",
            "a/b/mods/mid.jar",
            "lonely.jar",
            "a/b/c/d.txt",
            "overrides/config/sub/config/inner.toml"));

        // Node 实跑的完整有序结果（含同分时的插入序稳定性）
        var expected = new (string Root, int Score)[]
        {
            ("overrides", 119),
            ("", 99),
            (".minecraft/versions/1.21.11", 87),
            ("versions/1.21.11", 78),
            ("nested/overrides", 58),
            ("a/b", 58),
            ("a/b/c", 57),
            ("weird", 9),
        };

        Assert.Equal(expected.Length, r.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Root, r[i].Root);
            Assert.Equal(expected[i].Score, r[i].Score);
        }
    }

    [Fact]
    public void DetectContentRoots_同分时保持插入顺序_与Node的稳定sort一致()
    {
        // nested/overrides 与 a/b 同为 58 分：Node 的 sort 稳定，
        // 谁先出现在 entries 里谁排前面（这里 nested/overrides 在前）
        var r = ModpackReader.DetectContentRoots(FakeEntries(
            "nested/overrides/mods/n.jar", "a/b/mods/mid.jar"));

        Assert.Equal(58, r[0].Score);
        Assert.Equal(58, r[1].Score);
        Assert.Equal("nested/overrides", r[0].Root);
        Assert.Equal("a/b", r[1].Root);
    }

    [Fact]
    public void DetectContentRoots_无mods只有shaderpacks_排在末位()
    {
        var r = ModpackReader.DetectContentRoots(FakeEntries("weird/shaderpacks/s.zip"));

        Assert.Single(r);
        Assert.Equal("weird", r[0].Root);
        // 只有 1 个分类(10) - 深度1 = 9，且没有 mods 的 +50
        Assert.Equal(9, r[0].Score);
    }

    [Fact]
    public void DetectContentRoots_顶层散落目录_内容根为空串()
    {
        var r = ModpackReader.DetectContentRoots(FakeEntries("mods/top.jar", "config/top.toml"));

        Assert.Single(r);
        Assert.Equal("", r[0].Root);
        // 2 分类*10 + mods 50 + 空串30 - 深度0(''.split('/')=[''] 长度1)
        Assert.Equal(99, r[0].Score);
    }

    [Fact]
    public void DetectContentRoots_无任何受管目录_返回空()
    {
        Assert.Empty(ModpackReader.DetectContentRoots(FakeEntries("readme.txt", "a/b/c/d.txt")));
    }

    [Fact]
    public void DetectContentRoots_单层文件不参与_parts至少两段()
    {
        // "lonely.jar" 只有一个路径段，不可能作为内容根
        Assert.Empty(ModpackReader.DetectContentRoots(FakeEntries("lonely.jar")));
    }

    [Fact]
    public void DetectContentRoots_受管目录只在首个命中处break_不重复计入()
    {
        // "overrides/config/sub/config/inner.toml" 里 config 出现两次，
        // Node 的 break 保证只按第一个分类（config）算，parent 是 "overrides"
        var r = ModpackReader.DetectContentRoots(FakeEntries(
            "overrides/config/sub/config/inner.toml"));

        Assert.Single(r);
        Assert.Equal("overrides", r[0].Root);
        Assert.Equal(["config"], r[0].Categories);
    }

    [Fact]
    public void DetectContentRoots_versions目录仅一层版本名_才算命中()
    {
        // versions/1.21.11/mods 命中（+20），versions/1.21.11/extra/mods 不命中（正则 ^versions/[^/]+$）
        var r = ModpackReader.DetectContentRoots(FakeEntries(
            "versions/1.21.11/mods/a.jar",
            "versions/1.21.11/extra/mods/b.jar"));

        Assert.Equal(2, r.Count);
        Assert.Equal("versions/1.21.11", r[0].Root);
        Assert.Equal("versions/1.21.11/extra", r[1].Root);
        Assert.True(r[0].Score > r[1].Score);
    }

    // ==================================================================
    // loaderFromDependencies
    // ==================================================================

    /// <summary>
    /// 构造 dependencies 字典。
    ///
    /// <para><b>必须 Clone</b>：直接返回 <c>EnumerateObject()</c> 的元素会让
    /// <c>JsonElement</c> 指向已释放的 <c>JsonDocument</c>，一取值就抛
    /// <c>ObjectDisposedException</c>。生产代码里同样需要注意：
    /// <see cref="ModpackReader.LoaderFromDependencies"/> 拿到的元素必须
    /// 在 document 存活期间使用。</para>
    /// </summary>
    private static Dictionary<string, System.Text.Json.JsonElement> Deps(params (string, string)[] kv)
    {
        var s = "{" + string.Join(",", kv.Select(x => $"\"{x.Item1}\":\"{x.Item2}\"")) + "}";
        using var doc = System.Text.Json.JsonDocument.Parse(s);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    [Fact]
    public void LoaderFromDependencies_fabric_返回Fabric与版本()
    {
        var (loader, ver) = ModpackReader.LoaderFromDependencies(
            Deps(("minecraft", "1.21.11"), ("fabric-loader", "0.16.10")));
        Assert.Equal("Fabric", loader);
        Assert.Equal("0.16.10", ver);
    }

    [Fact]
    public void LoaderFromDependencies_多个加载器共存_按fabric优先()
    {
        // Node 的 map 键序即优先级：fabric→quilt→forge→neoforge
        var (loader, _) = ModpackReader.LoaderFromDependencies(
            Deps(("neoforge", "21.1.1"), ("forge", "52.0.1"), ("fabric-loader", "0.16.10")));
        Assert.Equal("Fabric", loader);
    }

    [Fact]
    public void LoaderFromDependencies_仅neoforge_返回NeoForge()
    {
        var (loader, ver) = ModpackReader.LoaderFromDependencies(Deps(("neoforge", "21.1.1")));
        Assert.Equal("NeoForge", loader);
        Assert.Equal("21.1.1", ver);
    }

    [Fact]
    public void LoaderFromDependencies_无加载器_返回空()
    {
        var (loader, ver) = ModpackReader.LoaderFromDependencies(Deps(("minecraft", "1.21.11")));
        Assert.Null(loader);
        Assert.Null(ver);
    }

    [Fact]
    public void LoaderFromDependencies_null依赖表_返回空()
    {
        var (loader, _) = ModpackReader.LoaderFromDependencies(null);
        Assert.Null(loader);
    }

    [Fact]
    public void LoaderFromDependencies_空字符串不视为命中()
    {
        // Node 是 `if (deps[k])` —— 空串 falsy
        var (loader, _) = ModpackReader.LoaderFromDependencies(Deps(("fabric-loader", "")));
        Assert.Null(loader);
    }

    [Fact]
    public void LoaderFromDependencies_数字型版本_转字符串()
    {
        // Node: String(deps[k]) 对数字同样生效（mrpack 里 loader 版本写成数字是合法的）
        using var doc = System.Text.Json.JsonDocument.Parse(
            """{ "neoforge": 21.1, "minecraft": 1.21 }""");
        var deps = doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

        var (loader, ver) = ModpackReader.LoaderFromDependencies(deps);
        Assert.Equal("NeoForge", loader);
        Assert.Equal("21.1", ver);
    }

    [Fact]
    public void LoaderFromDependencies_forge优先于neoforge_与原版键序一致()
    {
        // map 键序是 fabric→quilt→forge→neoforge，forge 排在 neoforge 前面，
        // 所以两者共存时命中的是 Forge —— 这个顺序容易被"想当然"改成 neoforge 优先
        var (loader, _) = ModpackReader.LoaderFromDependencies(
            Deps(("neoforge", "21.1.1"), ("forge", "52.0.1")));
        Assert.Equal("Forge", loader);
    }

    // ==================================================================
    // Open：不存在与非整合包
    // ==================================================================

    [Fact]
    public void Open_文件不存在_抛FileNotFound并带路径()
    {
        var ex = Assert.Throws<FileNotFoundException>(
            () => ModpackReader.Open(Path.Combine(Path.GetTempPath(), "不存在的整合包-xyz.zip")));
        Assert.Contains("整合包文件不存在", ex.Message);
    }

    [Fact]
    public void Open_无mods目录的zip_抛InvalidData()
    {
        var zip = WriteTemp(TestZips.MakeZip(("readme.txt", "hi")));
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => ModpackReader.Open(zip));
            Assert.Contains("没有找到 mods 文件夹", ex.Message);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    // ==================================================================
    // ListCategory
    // ==================================================================

    [Fact]
    public void ListCategory_内容根为空串_前缀不含前导斜杠()
    {
        // 【保真】空串 contentRoot 在 JS 里是 falsy，走 `${cat.dir}/` 分支。
        // 若C# 用 != null 判断，zip 根的整合包会一个文件都列不出来。
        using var inner = ZipReader.FromBuffer(TestZips.BuildMrpack(
            [("mods/a.jar", "x"), ("config/b.toml", "y")], mrpackContentRoot: ""));
        using var pack = MakeHandle(inner, contentRoot: "");

        var mods = pack.ListCategory("mods");
        Assert.Single(mods);
        Assert.Equal("a.jar", mods[0].RelPath);
    }

    [Fact]
    public void ListCategory_未知分类_返回空()
    {
        using var inner = ZipReader.FromBuffer(TestZips.BuildMrpack([("mods/a.jar", "x")], mrpackContentRoot: ""));
        using var pack = MakeHandle(inner, contentRoot: "");

        Assert.Empty(pack.ListCategory("saves"));
    }

    [Fact]
    public void ListCategory_正常情况_返回相对路径与指纹()
    {
        using var inner = ZipReader.FromBuffer(TestZips.BuildMrpack(
        [
            ("mods/sodium.jar", "jar-content-a"),
            ("mods/sub/deep.jar", "jar-content-b"),
            ("mods/notes.txt", "not a jar"),
            ("config/a.toml", "cfg"),
        ]));
        using var pack = MakeHandle(inner, contentRoot: "overrides");

        var mods = pack.ListCategory("mods");
        // notes.txt 不是 jar 但仍属于 mods 分类（.jar 过滤发生在 diffMods 里）
        Assert.Equal(3, mods.Count);
        Assert.Contains(mods, m => m.RelPath == "sub/deep.jar");
        Assert.Contains(mods, m => m.RelPath == "sodium.jar");
        Assert.All(mods, m => Assert.Equal($"{m.Size}:{m.Crc32}", m.Fingerprint));

        // 目录条目被排除
        Assert.DoesNotContain(mods, m => m.RelPath.EndsWith('/'));
    }

    [Fact]
    public void Open_mrpack全量包_读出元信息与分类统计()
    {
        var index = """
        {
          "name": "测试整合包",
          "versionId": "2.0.0",
          "dependencies": { "minecraft": "1.21.11", "fabric-loader": "0.16.10" },
          "files": []
        }
        """;
        var bytes = TestZips.BuildPcl2Outer(
        [
            ("mods/a.jar", "aaa"),
            ("mods/b.jar", "bbbb"),
            ("config/c.toml", "cc"),
            ("PCL/Setup.ini", "VersionVanillaName: 1.21.11\nVersionArgumentIndie: 1\n"),
        ],
        compressInner: false,          // STORED → 子区间直读快速路径
        indexJson: index);
        var path = WriteTemp(bytes);
        try
        {
            using var pack = ModpackReader.Open(path);
            Assert.Equal(ModpackFormat.Pcl2Mrpack, pack.Format);
            Assert.Equal("测试整合包", pack.PackName);
            Assert.Equal("2.0.0", pack.PackVersion);
            Assert.Equal("1.21.11", pack.GameVersion);
            Assert.Equal("Fabric", pack.Loader);
            Assert.Equal("0.16.10", pack.LoaderVersion);
            Assert.Equal("overrides", pack.ContentRoot);
            Assert.True(pack.IsolationHint);
            Assert.Equal(2, pack.CategoryCounts["mods"]);
            Assert.Equal(1, pack.CategoryCounts["config"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_没有SetupIni时隔离状态为null()
    {
        var bytes = TestZips.BuildPcl2Outer([("mods/a.jar", "aaa")], compressInner: false);
        var path = WriteTemp(bytes);
        try
        {
            using var pack = ModpackReader.Open(path);
            Assert.Null(pack.IsolationHint);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_版本号从SetupIni兜底_仅当index未提供()
    {
        var bytes = TestZips.BuildPcl2Outer(
            [("mods/a.jar", "aaa"), ("PCL/Setup.ini", "VersionVanillaName: 1.20.1\n")],
            compressInner: false,
            // index 里刻意不给 minecraft 依赖
            indexJson: """{ "name": "x", "dependencies": { "fabric-loader": "0.15.0" } }""");

        var path = WriteTemp(bytes);
        try
        {
            using var pack = ModpackReader.Open(path);
            Assert.Equal("1.20.1", pack.GameVersion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_压缩的内层mrpack_也能打开()
    {
        // 内层用 DEFLATE（method=8）走「小包进内存」分支
        var bytes = TestZips.BuildPcl2Outer([("mods/a.jar", "aaa")], compressInner: true);
        var path = WriteTemp(bytes);
        try
        {
            using var pack = ModpackReader.Open(path);
            Assert.Equal("overrides", pack.ContentRoot);
            Assert.Equal(1, pack.CategoryCounts["mods"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_直接是mrpack本体_格式标记为mrpack()
    {
        // 后缀是 .zip 但里面就是 modrinth.index.json
        var bytes = TestZips.BuildMrpack(
            [("overrides/mods/a.jar", "aaa")],
            indexJson: """{ "name": "本体包", "dependencies": { "minecraft": "1.21.11" } }""");
        var path = WriteTemp(bytes);
        try
        {
            using var pack = ModpackReader.Open(path);
            Assert.Equal(ModpackFormat.Mrpack, pack.Format);
            Assert.Equal("本体包", pack.PackName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_通用zip无mrpack_走内容根探测()
    {
        var bytes = TestZips.MakeZip(
            ("mods/a.jar", "aaa"),
            ("config/b.toml", "bb"),
            ("ignored/readme.txt", "hi"));
        var path = WriteTemp(bytes);
        try
        {
            using var pack = ModpackReader.Open(path);
            Assert.Equal(ModpackFormat.Generic, pack.Format);
            Assert.Equal("", pack.ContentRoot);
            Assert.Equal(1, pack.CategoryCounts["mods"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_包名去掉zip后缀_只去zip不其他扩展()
    {
        //【保真】Node 只 replace(/\.zip$/i)，"x.zip.zip" → "x.zip"
        var bytes = TestZips.MakeZip(("mods/a.jar", "aaa"));
        var path = Path.Combine(Path.GetTempPath(), "我的世界打包.zip.zip");
        File.WriteAllBytes(path, bytes);
        try
        {
            using var pack = ModpackReader.Open(path);
            // index 不存在 → 包名来自文件名
            Assert.Equal("我的世界打包.zip", pack.PackName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_进度回调_收到最终35百分比()
    {
        var bytes = TestZips.MakeZip(("mods/a.jar", "aaa"));
        var path = WriteTemp(bytes);
        try
        {
            var seen = new List<(string Msg, int Pct)>();
            using var pack = ModpackReader.Open(path, (m, p) => seen.Add((m, p)));
            Assert.Contains(seen, s => s.Pct == 35);
            Assert.Contains(seen, s => s.Msg.Contains("整合包读取完成"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Dispose_释放后读取抛ObjectDisposed()
    {
        var bytes = TestZips.MakeZip(("mods/a.jar", "aaa"));
        var path = WriteTemp(bytes);
        var pack = ModpackReader.Open(path);
        var entry = pack.ListCategory("mods")[0].Entry;
        pack.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pack.Read(entry));
        File.Delete(path);
    }

    // ------------------------------------------------------------------

    private static ModpackHandle MakeHandle(ZipReader reader, string contentRoot)
        => new(
            "test.zip", reader, null, null, false, ModpackFormat.Pcl2Mrpack,
            contentRoot, "test", null, null, null, null, null, null, []);

    private static string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcupdater-test-{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
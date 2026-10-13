using System.Text.Json;
using McUpdater.Core;
using Xunit;

namespace McUpdater.Tests;

/// <summary>
/// <see cref="ModMetadata"/> 与 <see cref="TomlLite"/> 测试。
///
/// <para><b>真实样本是这里的绝对主力</b>：<c>1.21.11 1.0.6.zip</c> 里
/// 90 个 mod、88 个走 <c>fabric.mod.json</c>、2 个落到文件名兜底。
/// 其中 <c>BetterGrassify</c> 的 <c>fabric.mod.json</c> 里有<em>裸换行</em>，
/// 是 <see cref="ModMetadata.SanitizeJson"/> 存在的唯一理由 ——
/// 没有这个样本，状态机就永远只是「看起来对」的代码。</para>
/// </summary>
public sealed class ModMetadataTests
{
    private const string PackPath = @"D:\Users\zhang\Downloads\1.21.11 1.0.6.zip";
    private static bool PackExists => File.Exists(PackPath);

    // ==================================================================
    // 真实样本：90 个 jar 全量解析
    // ==================================================================

    // 真实样本回归。期望值来自直接跑 Node 原版
    //（node -e "require('./src/core/mod-metadata')" 遍历 90 个 jar），不是我手算的：
    //   loader 分布: fabric 89 / neoforge 1（IMBlocker）
    //   全部 Guessed=false（没有一个落到文件名兜底）
    [Fact]
    public void 真实包_90个jar应全部解析出modId()
    {
        if (!PackExists) return;

        using var zip = ZipReader.FromFile(PackPath);
        var modEntries = zip.Entries
            .Where(e => !e.IsDir && e.Name.StartsWith("overrides/mods/", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(90, modEntries.Count);

        var infos = new List<ModInfo>();
        foreach (var e in modEntries)
        {
            var fileName = e.Name["overrides/mods/".Length..];
            infos.Add(ModMetadata.ParseJar(zip.Read(e), fileName));
        }

        // 全部拿得到 id（兜底也会给 id）
        Assert.All(infos, i => Assert.False(string.IsNullOrEmpty(i.Id)));
        // 本样本是纯 Fabric 整合包，只有 IMBlocker 一个走 NeoForge 的 mods.toml
        Assert.All(infos, i => Assert.Contains(i.Loader, new[] { "fabric", "neoforge" }));
    }

    [Fact]
    public void 真实包_loader分布应与Node原版一致为89fabric加1neoforge()
    {
        if (!PackExists) return;

        using var zip = ZipReader.FromFile(PackPath);
        var infos = new List<ModInfo>();
        foreach (var e in zip.Entries.Where(e =>
                     !e.IsDir && e.Name.StartsWith("overrides/mods/", StringComparison.Ordinal)))
        {
            infos.Add(ModMetadata.ParseJar(zip.Read(e), e.Name["overrides/mods/".Length..]));
        }

        // 【基准由 Node 原版跑出】fabric 89 / neoforge 1
        Assert.Equal(89, infos.Count(i => i.Loader == "fabric"));
        Assert.Equal(1, infos.Count(i => i.Loader == "neoforge"));

        // 唯一的 NeoForge 是 IMBlocker —— 它带 PCL2 中文前缀，
        // 靠的是 META-INF/neoforge.mods.toml 而非 fabric.mod.json
        var imblocker = Assert.Single(infos, i => i.Loader == "neoforge");
        Assert.Equal("imblocker", imblocker.Id);
        Assert.False(imblocker.Guessed);
        Assert.StartsWith("[输入法冲突修复]", imblocker.FileName);

        // 本样本里所有 mod 都解析成功，没有一个落到文件名兜底
        Assert.All(infos, i => Assert.False(i.Guessed));
        Assert.All(infos, i => Assert.Null(i.Error));
    }

    [Fact]
    public void 真实包_几个代表性mod的元数据应精确匹配()
    {
        if (!PackExists) return;

        using var zip = ZipReader.FromFile(PackPath);

        ModInfo Parse(string tail)
            => ModMetadata.ParseJar(zip.Read($"overrides/mods/{tail}"), tail);

        // 纯 ASCII 名，metadata 规整
        var fabricApi = Parse("fabric-api-0.141.5+1.21.11.jar");
        Assert.Equal("fabric-api", fabricApi.Id);
        Assert.Equal("Fabric API", fabricApi.Name);
        Assert.Equal("0.141.5+1.21.11", fabricApi.Version);
        Assert.Equal("fabric", fabricApi.Loader);
        Assert.False(fabricApi.Guessed);

        // 文件名带 mc 版本后缀，靠 metadata 纠正
        var boplenty = Parse("BiomesOPlenty-fabric-1.21.11-21.11.0.32.jar");
        Assert.Equal("biomesoplenty", boplenty.Id);
        Assert.Equal("21.11.0.32", boplenty.Version);

        // PCL2 中文前缀 —— 关键回归点
        var jei = Parse("[JEI物品管理器] jei-1.21.11-fabric-27.17.0.50.jar");
        Assert.Equal("jei", jei.Id);
        Assert.Equal("fabric", jei.Loader);
        Assert.Equal("[JEI物品管理器] jei-1.21.11-fabric-27.17.0.50.jar", jei.FileName);
        Assert.DoesNotContain('【', jei.Name);
    }

    [Fact]
    public void 真实包_BetterGrassify的裸换行JSON应被状态机救回()
    {
        if (!PackExists) return;

        // 这一个文件就是 SanitizeJson 存在的全部理由：
        // 它的 fabric.mod.json 的 description 里有个**裸换行**
        // （"...touch grass!?⏎OptiFine's..."），原生 JSON.parse 直接抛
        // Bad control character in string literal。
        using var zip = ZipReader.FromFile(PackPath);
        const string tail = "BetterGrassify-1.8.6+fabric.1.21.11.jar";

        // 先确认它真的含裸换行 —— 否则这条测试就变成自说自话。
        // 注意用 ThrowsAny：System.Text.Json 抛的是 JsonReaderException
        // （JsonException 的子类），Assert.Throws 要求精确类型会误判失败。
        var raw = ReadJarText(zip, tail, "fabric.mod.json");
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(raw));
        Assert.Contains("\n", raw);

        // 清洗后必须能解出正确的 id
        var info = ModMetadata.ParseJar(zip.Read($"overrides/mods/{tail}"), tail);
        Assert.Equal("bettergrass", info.Id);
        Assert.Equal("BetterGrassify", info.Name);
        Assert.Equal("1.8.6+fabric.1.21.11", info.Version);
        Assert.Equal("fabric", info.Loader);
        Assert.False(info.Guessed);
    }

    [Fact]
    public void SanitizeJson_绝不能砍掉URL里的双斜杠()
    {
        if (!PackExists) return;

        // 这是「必须用状态机、不能用正则」的直接实证：
        // 同一份 BetterGrassify 的 JSON 里同时有裸换行 **和** https:// URL。
        // 若用正则 `//.*$` 去注释，后半条 JSON 直接被砍光。
        using var zip = ZipReader.FromFile(PackPath);
        var raw = ReadJarText(zip, "BetterGrassify-1.8.6+fabric.1.21.11.jar", "fabric.mod.json");

        Assert.Contains("https://github.com/UltimatChamp/BetterGrassify", raw);

        var cleaned = ModMetadata.SanitizeJson(raw);
        Assert.Contains("https://github.com/UltimatChamp/BetterGrassify", cleaned);

        using var doc = JsonDocument.Parse(cleaned);
        Assert.Equal("bettergrass", doc.RootElement.GetProperty("id").GetString());
        // contact.issues 也是 https URL —— 清洗后必须还在
        Assert.Equal(
            "https://github.com/UltimatChamp/BetterGrassify/issues",
            doc.RootElement.GetProperty("contact").GetProperty("issues").GetString());
    }

    private static string ReadJarText(ZipReader pack, string tail, string entry)
    {
        var jarBytes = pack.Read($"overrides/mods/{tail}");
        using var jar = ZipReader.FromBuffer(jarBytes);
        return jar.ReadText(entry);
    }

    // ==================================================================
    // SanitizeJson 单元测试
    // ==================================================================

    [Fact]
    public void SanitizeJson_应转义字符串内的裸控制字符()
    {
        Assert.Equal("{\"a\":\"x\\ny\"}", ModMetadata.SanitizeJson("{\"a\":\"x\ny\"}"));
        Assert.Equal("{\"a\":\"x\\ry\"}", ModMetadata.SanitizeJson("{\"a\":\"x\ry\"}"));
        Assert.Equal("{\"a\":\"x\\ty\"}", ModMetadata.SanitizeJson("{\"a\":\"x\ty\"}"));
    }

    [Fact]
    public void SanitizeJson_应丢弃字符串内无法转义的控制字符()
    {
        // 0x01 是控制字符但 JSON 没有对应转义 → 直接丢掉
        Assert.Equal("{\"a\":\"xy\"}", ModMetadata.SanitizeJson("{\"a\":\"x\u0001y\"}"));
    }

    [Fact]
    public void SanitizeJson_应删除字符串外的行注释()
    {
        // 期望值取自 Node 原版实跑。**注意输出里那两个空格还在**
        //（"{\n  \n"）—— 因为状态机逐字符写出，碰到 // 时只是把游标
        // 推到换行符处换成一个 \n，注释<em>前面</em>的空格早就写过了。
        // 这看着像 bug，其实是逐字符照抄的结果，别"修"。
        var src = "{\n  // 这是注释\n  \"a\": 1\n}";
        Assert.Equal("{\n  \n  \"a\": 1\n}", ModMetadata.SanitizeJson(src));
    }

    [Fact]
    public void SanitizeJson_应删除字符串外的块注释()
    {
        var src = "{ /* 块\n注释 */ \"a\": 1 }";
        Assert.Equal("{  \"a\": 1 }", ModMetadata.SanitizeJson(src));
    }

    [Fact]
    public void SanitizeJson_不得动字符串内的注释符号()
    {
        // 三个都不能被当注释处理
        var src = "{\"url\":\"https://x.com\",\"c\":\"//不是注释\",\"d\":\"/*也不是*/\"}";
        Assert.Equal(src, ModMetadata.SanitizeJson(src));
    }

    [Fact]
    public void SanitizeJson_应保留转义序列()
    {
        var src = "{\"a\":\"x\\\"y\\n\\\\z\"}";
        Assert.Equal(src, ModMetadata.SanitizeJson(src));
    }

    // ==================================================================
    // LooseJsonParse
    // ==================================================================

    [Fact]
    public void LooseJsonParse_合法JSON应原样通过()
    {
        using var d = ModMetadata.LooseJsonParse("{\"a\":1,\"b\":[1,2]}");
        Assert.Equal(1, d.RootElement.GetProperty("a").GetInt32());
    }

    [Fact]
    public void LooseJsonParse_应去尾逗号()
    {
        using var d = ModMetadata.LooseJsonParse("{\"a\":1,}");
        Assert.Equal(1, d.RootElement.GetProperty("a").GetInt32());

        using var d2 = ModMetadata.LooseJsonParse("[1,2,3,]");
        Assert.Equal(3, d2.RootElement.GetArrayLength());
    }

    [Fact]
    public void LooseJsonParse_应去BOM()
    {
        using var d = ModMetadata.LooseJsonParse("\uFEFF{\"a\":1}");
        Assert.Equal(1, d.RootElement.GetProperty("a").GetInt32());
    }

    [Fact]
    public void LooseJsonParse_尾逗号清理不得伤及字符串内的逗号()
    {
        // 字符串里的 "}," 不能被当成尾逗号
        using var d = ModMetadata.LooseJsonParse("{\"a\":\"x,}\"}");
        Assert.Equal("x,}", d.RootElement.GetProperty("a").GetString());
    }

    [Fact]
    public void LooseJsonParse_仍非法时应抛Json异常()
    {
        // 清洗救不回来的就是真非法，不该静默返回 null
        Assert.ThrowsAny<JsonException>(() => ModMetadata.LooseJsonParse("{ 完全不是 JSON"));
    }

    // ==================================================================
    // parseFromFileName
    // ==================================================================

    /// <summary>
    /// 文件名兜底推断。
    ///
    /// <para><b>期望值全部由 Node 本人生成</b>（<c>node -e "...原函数..."</c>），
    /// 不是我手算的。这一点很关键：Node 的正则
    /// <c>^(.+?)[-_]v?(\d[\w.+\-]*)$</c> 有几处<em>反直觉</em>的输出，
    /// 我最初凭直觉写的期望值全错，见下面逐条注释。</para>
    /// </summary>
    [Theory]
    // 中文前缀必须剥掉。注意版本号是 "1.21.11-fabric-27.17.0.50" 而非 "27.17.0.50" ——
    // 正则非贪婪 + [\w.+\-]* 能吞掉 -，所以从第一个数字开始一直吃到结尾。
    // 【别当成 bug 去"修"】
    [InlineData("[JEI物品管理器] jei-1.21.11-fabric-27.17.0.50.jar", "jei", "1.21.11-fabric-27.17.0.50")]
    [InlineData("[MrCrayfish 的家具：重制] refurbished_furniture-fabric-1.21.11-1.0.23.jar",
        "refurbished_furniture", "1.21.11-1.0.23")]
    [InlineData("sodium-fabric-0.8.12+mc1.21.11.jar", "sodium", "0.8.12+mc1.21.11")]
    [InlineData("morechathistory-1.3.1.jar", "morechathistory", "1.3.1")]
    // 这里 version 是 "19.0.1-fabric"：因为剥掉 .jar 后整串都以数字开头，
    // 分隔符"-fabric"被 [\w.+\-]* 吞进版本里了。【Node 原样，别修】
    [InlineData("architectury-19.0.1-fabric.jar", "architectury", "19.0.1-fabric")]
    // .jar.disabled：先剥 .jar 失败（结尾是 .disabled），剥掉 .disabled 后
    // 剩下 "somemod-1.0.0.jar"，所以版本号里带着 ".jar"。
    // 【这是 Node 版真实行为，不是我实现错了——已用 node 验证】
    [InlineData("somemod-1.0.0.jar.disabled", "somemod", "1.0.0.jar")]
    [InlineData("modname-v2.3.4.jar", "modname", "2.3.4")]
    [InlineData("plainname.jar", "plainname", "未知")]
    [InlineData("AmbientSounds_FABRIC_v6.3.5_mc1.21.11.jar", "ambientsounds", "6.3.5_mc1.21.11")]
    public void 文件名兜底应与Node逐字节一致(string fileName, string expectId, string expectVersion)
    {
        var (id, version, guessed) = ModMetadata.ParseFromFileName(fileName);

        Assert.Equal(expectId, id);
        Assert.Equal(expectVersion, version);
        Assert.True(guessed);
    }

    [Fact]
    public void 文件名兜底应剥掉尾部加载器标识()
    {
        Assert.Equal("sodium", ModMetadata.ParseFromFileName("sodium-fabric-1.0.jar").Id);
        Assert.Equal("thing", ModMetadata.ParseFromFileName("thing-forge-1.0.jar").Id);
        Assert.Equal("thing", ModMetadata.ParseFromFileName("thing-neoforge-1.0.jar").Id);
        Assert.Equal("thing", ModMetadata.ParseFromFileName("thing-quilt-1.0.jar").Id);
        Assert.Equal("thing", ModMetadata.ParseFromFileName("thing-mc1.21.1.jar").Id);
    }

    [Fact]
    public void 非ASCII的id应被过滤掉或回落()
    {
        // 全部字符被过滤时回落用 base 的小写，不能返回空串
        var (id, _, _) = ModMetadata.ParseFromFileName("中文名-1.0.jar");
        Assert.NotEmpty(id);
    }

    // ==================================================================
    // ParseJar 的非 Fabric 分支（合成 jar）
    // ==================================================================

    [Fact]
    public void 应识别Quilt模组的quilt_mod_json()
    {
        var jar = BuildJar(("quilt.mod.json", """
            {
              "quilt_loader": {
                "group": "com.example",
                "id": "myquiltmod",
                "version": "2.0.0",
                "metadata": { "name": "My Quilt Mod" }
              },
              "quilt": { }
            }
            """));

        var info = ModMetadata.ParseJar(jar, "myquiltmod-2.0.0.jar");
        Assert.Equal("myquiltmod", info.Id);
        Assert.Equal("My Quilt Mod", info.Name);
        Assert.Equal("2.0.0", info.Version);
        Assert.Equal("quilt", info.Loader);
        Assert.False(info.Guessed);
    }

    [Fact]
    public void 应识别Forge模组的mcmod_info()
    {
        var jar = BuildJar(("mcmod.info", """
            [{ "modid": "oldforge", "name": "Old Forge Mod", "version": "1.2.3" }]
            """));

        var info = ModMetadata.ParseJar(jar, "oldforge-1.2.3.jar");
        Assert.Equal("oldforge", info.Id);
        Assert.Equal("Old Forge Mod", info.Name);
        Assert.Equal("1.2.3", info.Version);
        Assert.Equal("forge", info.Loader);
    }

    [Fact]
    public void 应识别mcmod_info的modList包装形式()
    {
        var jar = BuildJar(("mcmod.info", """
            { "modList": [ { "modid": "wrapped", "name": "Wrapped", "version": "3.0" } ], "version": "1" }
            """));

        var info = ModMetadata.ParseJar(jar, "wrapped-3.0.jar");
        Assert.Equal("wrapped", info.Id);
        Assert.Equal("3.0", info.Version);
    }

    [Fact]
    public void 应识别Forge模组的mods_toml()
    {
        var jar = BuildJar(("META-INF/mods.toml", """
            modLoader="javafml"
            loaderVersion="[47,)"
            license="MIT"

            [[mods]]
            modId="forgemod"
            version="4.5.6"
            displayName="Forge Mod"

            [[mods]]
            modId="secondmod"
            version="1.0"
            """));

        var info = ModMetadata.ParseJar(jar, "forgemod-4.5.6.jar");
        Assert.Equal("forgemod", info.Id);
        Assert.Equal("Forge Mod", info.Name);
        Assert.Equal("4.5.6", info.Version);
        Assert.Equal("forge", info.Loader);
    }

    [Fact]
    public void 应识别NeoForge模组且优先于Forge()
    {
        // 两个文件都存在时，neoforge.mods.toml 优先
        var jar = BuildJar(
            ("META-INF/neoforge.mods.toml", """
                [[mods]]
                modId="neoforgemod"
                version="5.0.0"
                displayName="NeoForge Mod"
                """),
            ("META-INF/mods.toml", """
                [[mods]]
                modId="forgemod"
                version="4.0.0"
                """));

        var info = ModMetadata.ParseJar(jar, "neoforgemod-5.0.0.jar");
        Assert.Equal("neoforgemod", info.Id);
        Assert.Equal("neoforge", info.Loader);
    }

    [Fact]
    public void 版本占位符应回落到MANIFEST_MF()
    {
        // 真实世界很常见：version 写 "${file.jarVersion}"
        var jar = BuildJar(
            ("fabric.mod.json", """
                { "schemaVersion":1, "id":"placeholdermod", "version":"${file.jarVersion}" }
                """),
            ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nImplementation-Version: 7.7.7\n\r\n"));

        var info = ModMetadata.ParseJar(jar, "placeholdermod-1.0.jar");
        Assert.Equal("7.7.7", info.Version);
    }

    [Fact]
    public void 占位符无解时应剔除占位符或给未知()
    {
        // 没有 MANIFEST.MF → 剔除后为空 → 「未知」
        var jar = BuildJar(("fabric.mod.json",
            """{ "schemaVersion":1, "id":"nomanifest", "version":"${file.jarVersion}" }"""));

        var info = ModMetadata.ParseJar(jar, "nomanifest-1.0.jar");
        Assert.Equal("未知", info.Version);

        // 占位符前后有内容 → 剔除后保留残余
        var jar2 = BuildJar(("fabric.mod.json",
            """{ "schemaVersion":1, "id":"partial", "version":"v${file.jarVersion}-x" }"""));
        Assert.Equal("v-x", ModMetadata.ParseJar(jar2, "partial-1.0.jar").Version);
    }

    [Fact]
    public void 非jar内容应回落到文件名且标记Guessed()
    {
        var notAZip = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        var info = ModMetadata.ParseJar(notAZip, "some-mod-1.2.3.jar");

        // Node 原版对同一输入的输出（node -e 验证）：id="somemod"
        Assert.Equal("somemod", info.Id);
        Assert.Equal("1.2.3", info.Version);
        Assert.True(info.Guessed);
        Assert.NotNull(info.Error);
    }

    [Fact]
    public void 损坏的fabric_json应回落到文件名()
    {
        // fabric.mod.json 存在但内容烂掉 → 抛异常被 catch → 兜底
        var jar = BuildJar(("fabric.mod.json", "{ 这不是 JSON"));

        var info = ModMetadata.ParseJar(jar, "brokenmod-1.0.jar");
        Assert.Equal("brokenmod", info.Id);
        Assert.True(info.Guessed);
        Assert.Equal("unknown", info.Loader);
    }

    // ==================================================================
    // TomlLite
    // ==================================================================

    [Fact]
    public void Toml_应解析多个mods条目()
    {
        var doc = TomlLite.Parse("""
            [[mods]]
            modId = "a"
            version = "1.0"
            displayName = "Mod A"

            [[mods]]
            modId = "b"
            version = "2.0"
            """);

        Assert.Equal(2, doc.Mods.Count);
        Assert.Equal("a", doc.Mods[0].ModId);
        Assert.Equal("Mod A", doc.Mods[0].DisplayName);
        Assert.Equal("1.0", doc.Mods[0].Version);
        Assert.Equal("b", doc.Mods[1].ModId);
        Assert.Null(doc.Mods[1].DisplayName);
    }

    [Fact]
    public void Toml_行尾注释不应伤及字符串内的井号()
    {
        var doc = TomlLite.Parse("""
            [[mods]]
            modId = "x"  # 这是注释
            displayName = "含#号的名称 # 不是注释"
            """);

        Assert.Equal("x", doc.Mods[0].ModId);
        Assert.Equal("含#号的名称 # 不是注释", doc.Mods[0].DisplayName);
    }

    [Fact]
    public void Toml_应支持三种引号()
    {
        var doc = TomlLite.Parse("""
            [[mods]]
            modId = "dq"
            displayName = 'sq'
            version = "line1\
            line2"
            """);
        // \" 基本双引号里的 \ + 换行（行continuation）不在支持范围，
        // 这里只验证前两个字段不受影响
        Assert.Equal("dq", doc.Mods[0].ModId);
        Assert.Equal("sq", doc.Mods[0].DisplayName);
    }

    [Fact]
    public void Toml_非mods表应被忽略()
    {
        var doc = TomlLite.Parse("""
            [[dependencies.forge]]
            modId = "shouldnotappear"

            [properties]
            modId = "alsonot"
            """);
        Assert.Empty(doc.Mods);
    }

    [Fact]
    public void Toml_空输入不应抛异常()
    {
        Assert.Empty(TomlLite.Parse(null).Mods);
        Assert.Empty(TomlLite.Parse("").Mods);
        Assert.Empty(TomlLite.Parse("全是注释 # 和空行").Mods);
    }

    [Fact]
    public void Toml_应解转义序列()
    {
        var doc = TomlLite.Parse("""
            [[mods]]
            modId = "tab\there"
            displayName = "quote\"inside"
            """);
        Assert.Equal("tab\there", doc.Mods[0].ModId);
        Assert.Equal("quote\"inside", doc.Mods[0].DisplayName);
    }

    // ==================================================================
    // 辅助
    // ==================================================================

    /// <summary>造一个内含指定条目的 jar（jar 就是 zip）。</summary>
    private static byte[] BuildJar(params (string Path, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var za = new System.IO.Compression.ZipArchive(
                   ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                var e = za.CreateEntry(path, System.IO.Compression.CompressionLevel.Optimal);
                using var w = e.Open();
                w.Write(System.Text.Encoding.UTF8.GetBytes(content));
            }
        }
        return ms.ToArray();
    }
}
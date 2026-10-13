using System.Text;
using McUpdater.Core;
using Xunit;

namespace McUpdater.Core.Tests;

/// <summary>
/// <b>端到端真实链路回归</b>：整合包 → 目标目录 → 完整 diff。
///
/// <para><b>样本</b>（Master 2026-10-09 提供，全程只读）：
/// <list type="bullet">
/// <item>整合包：<c>D:/Users/zhang/Downloads/1.21.11 1.0.6.zip</c>
///       —— Modrinth 全量打包（<c>files: []</c>，内容全在 <c>overrides/</c>）</item>
/// <item>目标目录：<c>E:/mc/pcl2/.minecraft/versions/Fabulously Optimized</c>
///       —— PCL2 里另一个完全不同的整合包（50 个 jar）</item>
/// </list></para>
///
/// <para><b>为什么这个组合比"同包自比"更有价值</b>：两个整合包毫无血缘关系，
/// 所以产出的 106 项变更覆盖了 added / updated / removed / unchanged 四种类型，
/// 还包含中文文件名、PCL2 前缀、版本降级等真实边界。若只做"同包自比"，
/// 全部命中指纹预筛走unchanged 分支，什么都验不到。</para>
///
/// <para><b>期望值全部来自 Node 原版实跑</b>（<c>tools/gen-real-baseline.js</c>
/// → <c>tools/real-baseline.json</c>）。C# 侧必须逐项对齐，包括
/// <b>变更顺序</b>与<b>默认勾选状态</b>——这两项都是用户直接可见的行为。
/// 基准里内嵌的 106 条顺序断言就是防"看起来对但展示顺序变了"的护栏。</para>
///
/// <para><b>样本缺失时全部跳过</b>：换机器 / 换路径时测试不应当红。
/// 但只要样本在，就必须真的跑—— <c>Assert.Skip</c> 不是这里该用的，
/// 用静默 return 让"样本悄悄不在了"和"行为变了"有明确区分。</para>
/// </summary>
public class RealDiffTests
{
    private const string PackPath = @"D:\Users\zhang\Downloads\1.21.11 1.0.6.zip";
    private const string TargetVersion = @"E:\mc\pcl2\.minecraft\versions\Fabulously Optimized";

    private static bool Ready => File.Exists(PackPath) && Directory.Exists(Path.Combine(TargetVersion, "mods"));

    /// <summary>
    /// Node 实跑的黄金顺序，<b>完整 106 条</b>（格式 <c>type|key|fileName|checked</c>）。
    /// 由 <c>tools/gen-real-baseline.js</c> 产出后逐条誊入。
    ///
    /// <para><b>这是整份测试里最硬的一条断言</b>：任何一项对不上都说明移植走偏。
    /// 尤其"顺序"与"勾选状态"—— 前者是 UI 展示顺序，后者直接决定
    /// 一次误操作会删掉玩家哪些文件。</para>
    /// </summary>
    private static readonly string[] ExpectedOrder =
    [
        "added|id:ambientsounds|AmbientSounds_FABRIC_v6.3.5_mc1.21.11.jar|1",
        "updated|id:animatica|animatica-0.6.1+1.21.5.jar|1",
        "added|id:architectury|architectury-19.0.1-fabric.jar|1",
        "added|id:betterblockentities|bbe-fabric-1.3.4+mc1.21.11.jar|1",
        "added|id:bettergrass|BetterGrassify-1.8.6+fabric.1.21.11.jar|1",
        "updated|id:bettermounthud|bettermounthud-1.2.6.jar|1",
        "added|id:biomesoplenty|BiomesOPlenty-fabric-1.21.11-21.11.0.32.jar|1",
        "added|id:cape-provider|cape-provider-5.1.0.jar|1",
        "added|id:cardinal-components|cardinal-components-api-7.3.2.jar|1",
        "added|id:carryon|carryon-fabric-1.21.11-2.9.0.jar|1",
        "updated|id:cloth-config|cloth-config-21.11.153-fabric.jar|1",
        "added|id:config_manager|ConfigManager-fabric-1.21.x-1.1.1.jar|1",
        "updated|id:continuity|continuity-3.0.1-beta.1+1.21.11.jar|1",
        "updated|id:controlify|controlify-3.0.0+lts+1.21.11-fabric.jar|1",
        "added|id:crash_assistant|CrashAssistant-fabric-1.21.5-1.21.11-1.11.9.jar|1",
        "added|id:creativecore|CreativeCore_FABRIC_v2.14.11_mc1.21.11.jar|1",
        "added|id:customskinloader-bootstrap|CustomSkinLoader_Universal-15.0.1.jar|1",
        "added|id:cubes-without-borders|cwb-fabric-3.0.0-build.14+mc1.21.11.jar|1",
        "updated|id:debugify|debugify-1.21.11+1.1.jar|1",
        "updated|id:dynamic_fps|dynamic-fps-3.11.6+minecraft-1.21.11-fabric.jar|1",
        "added|id:e4mc|e4mc-fabric-6.1.1.jar|1",
        "updated|id:entityculling|entityculling-fabric-1.10.2-mc1.21.11.jar|1",
        "updated|id:entity_model_features|entity_model_features-3.2.4-1.21.11-fabric.jar|1",
        "updated|id:entity_texture_features|entity_texture_features_1.21.11-fabric-7.1.jar|1",
        "updated|id:fabric-api|fabric-api-0.141.5+1.21.11.jar|1",
        "updated|id:fabric-language-kotlin|fabric-language-kotlin-1.13.12+kotlin.2.4.0.jar|1",
        "updated|id:fabrishot|fabrishot-1.16.4.jar|1",
        "added|id:fallingtree|FallingTree-1.21.11-1.21.11.3.jar|1",
        "added|id:farmersdelight|FarmersDelight-1.21.11-3.4.9+refabricated.jar|1",
        "added|id:fast-leaf-decay|fast-leaf-decay-fabric-1.0.0.jar|1",
        "updated|id:fastquit|fastquit-3.1.3+mc1.21.11.jar|1",
        "updated|id:ferritecore|ferritecore-8.2.0-fabric.jar|1",
        "added|id:flora|flora-1.0.4-1.21.11.jar|1",
        "added|id:forgeconfigapiport|ForgeConfigAPIPort-v21.11.1-mc1.21.11-Fabric.jar|1",
        "added|id:framework|framework-fabric-1.21.11-0.13.11.jar|1",
        "added|id:fuji|fuji-fabric-14.10.0-e22ce07ff0-mc1.21.11.jar|1",
        "added|id:furniture_mod_tools_refurbished|furniture_mod_tools_refurbished-1.0.0-fabric-1.21.11.jar|1",
        "added|id:glitchcore|GlitchCore-fabric-1.21.11-21.11.0.4.jar|1",
        "updated|id:immediatelyfast|ImmediatelyFast-Fabric-1.14.2+1.21.11.jar|1",
        "updated|id:iris|iris-fabric-1.10.7+mc1.21.11.jar|1",
        "added|id:ixeris|Ixeris-4.4.1+1.21.11-fabric.jar|1",
        "added|id:journeymap|journeymap-fabric-1.21.11-6.0.0.jar|1",
        "updated|id:lambdynlights|lambdynamiclights-4.9.1+1.21.11.jar|1",
        "updated|id:languagereload|language-reload-1.7.6+1.21.11.jar|1",
        "added|id:libipn|libIPN-fabric-1.21.11-6.6.3.jar|1",
        "updated|id:lithium|lithium-fabric-0.21.4+mc1.21.11.jar|1",
        "added|id:lithostitched|lithostitched-1.7.2-fabric-21.11.jar|1",
        "updated|id:isxander-main-menu-credits|main-menu-credits-1.2.0.jar|1",
        "added|id:mcwfurnitures|mcw-furniture-3.4.2-mc1.21.11fabric.jar|1",
        "unchanged|id:mixintrace|mixintrace-1.1.1+1.17.jar|0",
        "updated|id:modmenu|modmenu-17.0.0.jar|1",
        "updated|id:morechathistory|morechathistory-1.3.1.jar|1",
        "updated|id:moreculling|moreculling-fabric-1.21.11-1.6.2.jar|1",
        "updated|id:optigui|optigui-2.3.0-beta.10+1.21.9.jar|1",
        "added|id:paginatedadvancements|paginatedadvancements-2.8.0+1.21.11.jar|1",
        "added|id:particlerain|particlerain-4.0.0-beta.10+1.21.11-fabric.jar|1",
        "added|id:polytone|polytone-fabric-1.21.11-5.7.2.jar|1",
        "added|id:pop_up_emotes|pop_up_emotes-1.5.1.jar|1",
        "added|id:presencefootsteps|PresenceFootsteps-1.12.4+1.21.11.jar|1",
        "updated|id:puzzle|puzzle-fabric-2.3.0+1.21.11.jar|1",
        "added|id:puzzleslib|PuzzlesLib-v21.11.13-mc1.21.11-Fabric.jar|1",
        "updated|id:reeses-sodium-options|reeses-sodium-options-fabric-2.0.3+mc1.21.11.jar|1",
        "updated|id:rrls|rrlsFabric-5.1.15+mc.1.21.11.jar|1",
        "added|id:sereneseasons|SereneSeasons-fabric-1.21.11-21.11.0.4.jar|1",
        "added|id:server_i18n_api|server_i18n_api-1.5.2-1.21.11-fabric.jar|1",
        "added|id:skinlayers3d|skinlayers3d-fabric-1.11.2-mc1.21.11.jar|1",
        "added|id:skyboxify|skyboxify-2.8+1.21.11-fabric.jar|1",
        "updated|id:sodium-extra|sodium-extra-fabric-0.8.3+mc1.21.11.jar|1",
        "added|id:sspb|sodium-shadowy-path-blocks-fabric-6.0.0.jar|1",
        "added|id:terrablender|TerraBlender-fabric-1.21.11-21.11.0.0.jar|1",
        "added|id:voicechat|voicechat-fabric-1.21.11-2.6.20.jar|1",
        "added|id:voxy|voxy-0.2.16-beta+1.21.11.jar|1",
        "updated|id:yet_another_config_lib_v3|yet_another_config_lib_v3-3.8.2+1.21.11-fabric.jar|1",
        "updated|id:zoomify|zoomify-2.15.2+1.21.11.jar|1",
        "added|id:jei|[JEI物品管理器] jei-1.21.11-fabric-27.17.0.50.jar|1",
        "added|id:refurbished_furniture|[MrCrayfish 的家具：重制] refurbished_furniture-fabric-1.21.11-1.0.23.jar|1",
        "added|id:inventoryprofilesnext|[一键背包整理Next] InventoryProfilesNext-fabric-1.21.11-2.2.6.jar|1",
        "added|id:dynamiccrosshair|[动态准星] dynamiccrosshair-9.11+1.21.11-fabric.jar|1",
        "added|id:pickupnotifier|[拾取提示] PickUpNotifier-v21.11.0-mc1.21.11-Fabric.jar|1",
        "added|id:betteradvancements|[更好的进度] BetterAdvancements-Fabric-1.21.11-0.4.8.54.jar|1",
        "added|id:brbe|[更好的配方书（续作）] BetterRecipeBookExtended-fabric-1.21.11-2.2.jar|1",
        "added|id:immersive_aircraft|[沉浸式飞机] immersive_aircraft-1.4.7+1.21.11-fabric.jar|1",
        "added|id:jade|[玉 🔍] Jade-1.21.11-Fabric-21.1.6.jar|1",
        "added|id:biologydictionary|[生物辞典] biology-dictionary-1.0.4-mc1.21.11-fabric.jar|1",
        "updated|id:nochatreports|[禁用聊天举报] NoChatReports-FABRIC-1.21.11-v2.18.0.jar|1",
        "added|id:chatanimation|[聊天动画] chatanimation-fabric-1.3.0+mc1.21.11.jar|1",
        "added|id:chat_heads|[聊天头像] chat_heads-1.2.5-fabric-1.21.11.jar|1",
        "added|id:imblocker|[输入法冲突修复] IMBlocker-6.1.4-neoforge+1.21.9-1.21.11.jar|1",
        "added|id:uml|[通用模组汉化器] zz-Universal_Mod_Localizer_2.0.3.jar|1",
        "updated|id:sodium|[钠] sodium-fabric-0.8.12+mc1.21.11.jar|1",
        "removed|id:advancementinfo|advancementinfo-1.20-fabric0.83.0-1.4.jar|0",
        "removed|id:antighost|antighost-1.20-fabric0.83.0-1.1.5.jar|0",
        "removed|id:borderlessmining|borderless-mining-1.1.8+1.20.1.jar|0",
        "removed|id:capes|capes-1.5.2+1.20-fabric.jar|0",
        "removed|id:citresewn|CITResewn-1.1.3+1.20.jar|0",
        "removed|id:e4mc_minecraft|e4mc-4.0.1+1.19.4-fabric.jar|0",
        "removed|id:enhancedblockentities|enhancedblockentities-0.9+1.20.jar|0",
        "removed|id:fabricskyboxes|fabricskyboxes-0.7.2+mc1.20.1.jar|0",
        "removed|id:fadeless|fadeless-1.0.1.jar|0",
        "removed|id:holdthatchunk|farsight-fabric-1.20.1-4.1.jar|0",
        "removed|id:fsb-interop|fsb-interop-1.3.4+mc1.20-build.47.jar|0",
        "removed|id:indium|indium-1.0.25+mc1.20.1.jar|0",
        "removed|id:memoryleakfix|memoryleakfix-fabric-1.17+-1.1.2.jar|0",
        "removed|id:modelfix|modelfix-1.14-fabric.jar|0",
        "removed|id:modernfix|modernfix-fabric-5.7.2+mc1.20.1.jar|0",
        "removed|id:yosbr|yosbr-0.1.2.jar|0"
    ];

    /// <summary>Node 实跑：按类型的变更数量分布。</summary>
    private static readonly Dictionary<string, int> ExpectedModsByType = new()
    {
        ["added"] = 56,
        ["updated"] = 33,
        ["removed"] = 16,
        ["unchanged"] = 1,
    };

    // ==================================================================
    // 整合包解析
    // ==================================================================

    [Fact]
    public void 真实包_打开整合包_元信息与Node基准一致()
    {
        if (!Ready) return;

        using var pack = ModpackReader.Open(PackPath);

        // 这是 mrpack 本体（后缀被改成 .zip），不是 PCL2 双层包装
        Assert.Equal(ModpackFormat.Mrpack, pack.Format);
        Assert.Equal("1.21.11", pack.PackName);
        Assert.Equal("1.0.6", pack.PackVersion);
        Assert.Equal("1.21.11", pack.GameVersion);
        Assert.Equal("Fabric", pack.Loader);
        Assert.Equal("0.19.3", pack.LoaderVersion);
        Assert.Equal("overrides", pack.ContentRoot);
        Assert.Equal("1.21.11", pack.VersionName);
        Assert.True(pack.IsolationHint);
    }

    [Fact]
    public void 真实包_分类统计与Node基准一致()
    {
        if (!Ready) return;

        using var pack = ModpackReader.Open(PackPath);

        Assert.Equal(90, pack.CategoryCounts["mods"]);
        Assert.Equal(191, pack.CategoryCounts["config"]);
        Assert.Equal(8, pack.CategoryCounts["resourcepacks"]);
        Assert.Equal(3, pack.CategoryCounts["shaderpacks"]);
    }

    [Fact]
    public void 真实包_非mrpack路径_不产出根候选()
    {
        if (!Ready) return;

        using var pack = ModpackReader.Open(PackPath);
        // 有 modrinth.index.json → contentRoot 直接是 overrides，不需要探测
        Assert.Empty(pack.RootCandidates);
    }

    // ==================================================================
    // mods 全链路对比
    // ==================================================================

    [Fact]
    public void 真实包_mods对比_变更数量与类型分布与Node基准一致()
    {
        if (!Ready) return;

        using var pack = ModpackReader.Open(PackPath);
        var zipMods = pack.ListCategory("mods");
        var r = DiffEngine.DiffMods(zipMods, Path.Combine(TargetVersion, "mods"), pack, null, null);

        Assert.Equal(90, zipMods.Count);
        Assert.Equal(106, r.Changes.Count);

        var byType = r.Changes
            .GroupBy(c => c.Type)
            .ToDictionary(g => g.Key, g => g.Count());

        // 【保真】Node 的 loader 权威基准是 fabric=89 / neoforge=1，
        // 所以这里不该出现任何解析不出 id 的 file: 兜底 key
        foreach (var (type, count) in ExpectedModsByType)
        {
            Assert.Equal(count, byType.GetValueOrDefault(type, 0));
        }

        Assert.Empty(r.Conflicts);
    }

    [Fact]
    public void 真实包_mods对比_全部按modId匹配_无file兜底()
    {
        if (!Ready) return;

        using var pack = ModpackReader.Open(PackPath);
        var r = DiffEngine.DiffMods(pack.ListCategory("mods"),
            Path.Combine(TargetVersion, "mods"), pack, null, null);

        // 【关键】PCL2 会给 mod 加中文前缀，若按文件名匹配就会出现 file: 开头的 key。
        // 全部是 id: 说明 jar 内元数据解析与 modId 匹配都正常工作。
        Assert.All(r.Changes, c => Assert.StartsWith("id:", c.Key));
    }

    [Fact]
    public void 真实包_mods对比_顺序与勾选状态逐项对齐Node基准()
    {
        if (!Ready) return;

        using var pack = ModpackReader.Open(PackPath);
        var r = DiffEngine.DiffMods(pack.ListCategory("mods"),
            Path.Combine(TargetVersion, "mods"), pack, null, null);

        var actual = r.Changes
            .Select(c => $"{c.Type}|{c.Key}|{c.FileName}|{(c.Checked ? 1 : 0)}")
            .ToArray();

        // 全量逐项比对：106 条，包括顺序与勾选状态。
        // 用 Assert.Equal 比整个数组，失败时会把第一处不同的下标和值一起打出来，
        // 比循环里逐条断言更好定位。
        Assert.Equal(ExpectedOrder, actual);

        // 额外确认结构性质：末尾 10 条必须全是"移除且不勾选"
        var tail = actual.TakeLast(10).ToArray();
        Assert.All(tail, s => Assert.StartsWith("removed|", s));
        Assert.All(tail, s => Assert.EndsWith("|0", s));
    }

    [Fact]
    public void 真实包_移除项默认不勾选_无manifest时不删玩家东西()
    {
        if (!Ready) return;

        using var pack = ModpackReader.Open(PackPath);
        // 目标目录里没有 .mcupdater-manifest.json（Node 基准确认 manifest=null）
        Assert.Null(Manifest.Load(TargetVersion));

        var r = DiffEngine.DiffMods(pack.ListCategory("mods"),
            Path.Combine(TargetVersion, "mods"), pack, null, null);

        var removed = r.Changes.Where(c => c.Type == ChangeType.Removed).ToList();
        Assert.Equal(16, removed.Count);

        // 【最重要的一条安全断言】无 manifest = 无从判断归属 = 一律不勾。
        // 若这里变成 true，一次「更新」就会删掉玩家 16 个自装 mod。
        Assert.All(removed, c => Assert.False(c.Checked));
        Assert.All(removed, c => Assert.Null(c.PlayerInstalled));
    }

    [Fact]
    public void 真实包_新增项默认勾选且带版本号()
    {
        if (!Ready) return;

        using var pack = ModpackReader.Open(PackPath);
        var r = DiffEngine.DiffMods(pack.ListCategory("mods"),
            Path.Combine(TargetVersion, "mods"), pack, null, null);

        var added = r.Changes.Where(c => c.Type == ChangeType.Added).ToList();
        Assert.Equal(56, added.Count);
        Assert.All(added, c =>
        {
            Assert.True(c.Checked);
            Assert.False(string.IsNullOrEmpty(c.NewVersion));
            Assert.Null(c.OldVersion);
            Assert.False(string.IsNullOrEmpty(c.ZipRelPath));
        });
    }

    [Fact]
    public void 真实包_中文文件名正确解码()
    {
        if (!Ready) return;

        using var pack = ModpackReader.Open(PackPath);
        var mods = pack.ListCategory("mods");

        // 真实包里有大量 PCL2 中文前缀文件名，解码错会变成乱码
        Assert.Contains(mods, m => m.RelPath.StartsWith("["));
        Assert.All(mods, m => Assert.DoesNotContain("�", m.RelPath));
    }

    [Fact]
    public void 真实包_重复对比结果稳定_缓存不改变行为()
    {
        if (!Ready) return;

        using var pack = ModpackReader.Open(PackPath);
        var zipMods = pack.ListCategory("mods");
        var localMods = Path.Combine(TargetVersion, "mods");

        var first = DiffEngine.DiffMods(zipMods, localMods, pack, null, null);
        var cache = new MetaCache();
        var second = DiffEngine.DiffMods(zipMods, localMods, pack, null, cache);
        var third = DiffEngine.DiffMods(zipMods, localMods, pack, null, cache);

        string Sig(DiffResult r) => string.Join("\n",
            r.Changes.Select(c => $"{c.Type}|{c.Key}|{c.FileName}|{c.Checked}|{c.OldVersion}|{c.NewVersion}"));

        Assert.Equal(Sig(first), Sig(second));
        Assert.Equal(Sig(second), Sig(third));
    }

    // ==================================================================
    // 普通文件分类
    // ==================================================================

    [Fact]
    public void 真实包_config对比_与Node基准一致()
    {
        if (!Ready) return;

        using var pack = ModpackReader.Open(PackPath);
        var r = DiffEngine.DiffFiles("config", pack.ListCategory("config"),
            Path.Combine(TargetVersion, "config"), null);

        Assert.Equal(190, r.Changes.Count);
        Assert.Equal(115, r.Changes.Count(c => c.Type == ChangeType.Added));
        Assert.Equal(28, r.Changes.Count(c => c.Type == ChangeType.Updated));
        Assert.Equal(6, r.Changes.Count(c => c.Type == ChangeType.Unchanged));
        Assert.Equal(41, r.Changes.Count(c => c.Type == ChangeType.Removed));

        // 43 个受保护文件（崩溃报告 / 披风配置 / puzzle 缓存等）不参与对比
        Assert.Equal(43, r.Skipped!.Protected);
        Assert.Equal(0, r.Skipped.Oversize);

        // 样本上限 6（Node 里同样是 6）
        Assert.Equal(6, r.Skipped.Samples.Count);
    }

    [Fact]
    public void 真实包_config变更_默认不勾选_防丢玩家调参()
    {
        if (!Ready) return;

        using var pack = ModpackReader.Open(PackPath);
        var r = DiffEngine.DiffFiles("config", pack.ListCategory("config"),
            Path.Combine(TargetVersion, "config"), null);

        // 【保真】Node: config 的 updated 一律 checked=false
        Assert.All(r.Changes.Where(c => c.Type == ChangeType.Updated), c => Assert.False(c.Checked));
        // 但新增是勾上的 —— 整合包带来的新配置直接写
        Assert.All(r.Changes.Where(c => c.Type == ChangeType.Added), c => Assert.True(c.Checked));
        // 移除项永不自动勾
        Assert.All(r.Changes.Where(c => c.Type == ChangeType.Removed), c => Assert.False(c.Checked));
    }

    [Fact]
    public void 真实包_config保护规则_崩溃报告等不出现在变更里()
    {
        if (!Ready) return;

        using var pack = ModpackReader.Open(PackPath);
        var r = DiffEngine.DiffFiles("config", pack.ListCategory("config"),
            Path.Combine(TargetVersion, "config"), null);

        // 这些前缀在 CATEGORY_PROTECTED 里，一条都不该出现在变更清单
        foreach (var c in r.Changes)
        {
            Assert.False(c.Key.StartsWith("crash_assistant/"), $"崩溃报告泄漏: {c.Key}");
            Assert.False(c.Key.StartsWith("cape-provider/"), $"账号绑定配置泄漏: {c.Key}");
            Assert.False(c.Key.StartsWith("voicechat/"), $"本机硬件绑定配置泄漏: {c.Key}");
            Assert.False(c.Key.StartsWith("e4mc/"), $"联机凭据泄漏: {c.Key}");
            Assert.False(c.Key.StartsWith(".puzzle_cache/"), $"缓存泄漏: {c.Key}");
            Assert.False(c.Key.EndsWith(".zip"), $"备份压缩包泄漏: {c.Key}");
        }
    }

    [Fact]
    public void 真实包_资源包对比_只认zip且更新可勾选()
    {
        if (!Ready) return;

        using var pack = ModpackReader.Open(PackPath);
        var r = DiffEngine.DiffFiles("resourcepacks", pack.ListCategory("resourcepacks"),
            Path.Combine(TargetVersion, "resourcepacks"), null);

        Assert.Equal(9, r.Changes.Count);
        Assert.Equal(5, r.Changes.Count(c => c.Type == ChangeType.Added));
        Assert.Equal(3, r.Changes.Count(c => c.Type == ChangeType.Updated));
        Assert.Equal(1, r.Changes.Count(c => c.Type == ChangeType.Removed));

        // 资源包可以放心覆盖 → 与 config 不同，updated 是勾上的
        Assert.All(r.Changes.Where(c => c.Type == ChangeType.Updated), c => Assert.True(c.Checked));
    }

    [Fact]
    public void 真实包_光影包对比_与Node基准一致()
    {
        if (!Ready) return;

        using var pack = ModpackReader.Open(PackPath);
        var r = DiffEngine.DiffFiles("shaderpacks", pack.ListCategory("shaderpacks"),
            Path.Combine(TargetVersion, "shaderpacks"), null);

        Assert.Equal(3, r.Changes.Count);
        Assert.All(r.Changes, c => Assert.Equal(ChangeType.Added, c.Type));
        Assert.Equal(
            ["photon_v1.3b.zip", "serendipity v0.2.3.zip", "truelightfx v5.1.2.zip"],
            r.Changes.Select(c => c.Key));
    }

    [Fact]
    public void 真实包_资源包带空格的路径_能正确处理()
    {
        if (!Ready) return;

        using var pack = ModpackReader.Open(PackPath);
        var r = DiffEngine.DiffFiles("resourcepacks", pack.ListCategory("resourcepacks"),
            Path.Combine(TargetVersion, "resourcepacks"), null);

        // 真实包里有 "1.21.11-quadral v.6.5.3.zip"、"catppuccin mocha blue.zip" 这类
        // 带空格的资源包名，也含中文名（"[翻译补充]cfmr-v0.4.zip"）
        Assert.Contains(r.Changes, c => c.Key == "1.21.11-quadral v.6.5.3.zip");
        Assert.Contains(r.Changes, c => c.Key == "catppuccin mocha blue.zip");
        Assert.Contains(r.Changes, c => c.Key.Contains("[翻译补充]"));
    }

    // ==================================================================
    // 进度回调
    // ==================================================================

    [Fact]
    public void 真实包_打开与对比_进度回调覆盖关键节点()
    {
        if (!Ready) return;

        var openPcts = new List<int>();
        using (var pack = ModpackReader.Open(PackPath, (_, pct) => openPcts.Add(pct)))
        {
            Assert.Equal(5, openPcts[0]);
            Assert.Equal(35, openPcts[^1]);

            var diffPcts = new List<int>();
            _ = DiffEngine.DiffMods(pack.ListCategory("mods"),
                Path.Combine(TargetVersion, "mods"), pack, null, null, (_, pct) => diffPcts.Add(pct));
            Assert.Equal([45, 55, 70], diffPcts);
        }
    }
}
using McUpdater.Core;
using Xunit;

namespace McUpdater.Core.Tests;

/// <summary>
/// <c>DiffEngine</c> 测试。
///
/// <para><b>核心场景（A/B/C/D）的期望值全部来自 Node 原版实跑</b>
/// ——由 <c>tools/gen-diff-baseline.js</c> 生成、再与
/// <c>tools/gen-diff-baseline.json</c> 逐字段核对。这些基准里有大量
/// 反直觉但必须照抄的行为：</para>
/// <list type="bullet">
/// <item>变更顺序 = zip 侧插入序，然后才是本地侧独有的 key；</item>
/// <item>无 manifest 时 <c>playerInstalled</c> 是 <c>null</c> 而非 false；</item>
/// <item>有 manifest 时玩家自装的 mod 判为 <c>playerInstalled=true</c> 且不勾选；</item>
/// <item>本地重复项里「副本」文件会被选为代表项，正规文件反而进移除列表；</item>
/// <item>内容一致仅文件名不同 → 判unchanged 且 <c>renamed=true</c>。</item>
/// </list>
/// </summary>
public class DiffEngineTests
{
    // ==================================================================
    // CompareVersion —— 基准来自 Node 实跑（27 组）
    // ==================================================================

    [Theory]
    // 基础
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("0.8.12", "0.8.13", -1)]
    [InlineData("0.8.13", "0.8.12", 1)]
    [InlineData("1.0.0", "2.0.0", -1)]
    [InlineData("2.0.0", "1.0.0", 1)]
    // parseInt 陷阱：'12abc' → 12（不是 NaN），所以判"相等"
    [InlineData("1.0.12abc", "1.0.12", 0)]
    [InlineData("1.0.12", "1.0.12abc", 0)]
    // 非数字段 = NaN = -1，比任何数字都小
    [InlineData("1.0.0-beta", "1.0.0", 0)]
    [InlineData("1.0.0", "1.0.0-beta", 0)]
    [InlineData("1.0.0_alpha", "1.0.0", 0)]
    [InlineData("1.0.0", "1.0.0_alpha", 0)]
    // 前缀段：'fabric' → NaN(-1)，所以判小
    [InlineData("fabric-0.16.10", "0.16.10", -1)]
    [InlineData("v1.2.3", "1.2.3", -1)]
    [InlineData("1.2.3", "v1.2.3", 1)]
    // 【反直觉】'+' 也是分隔符，'build' 段变NaN 后 7 比 -1 大 → 判"升级"
    [InlineData("1.0.0+build.7", "1.0.0", 1)]
    [InlineData("1.0.0", "1.0.0+build.7", -1)]
    // 越界段等价于 NaN
    [InlineData("1.0.0.0.0.1", "1.0.0.0.0.2", -1)]
    [InlineData("1.0.0", "1.0.0.0.1", -1)]
    [InlineData("1.0.0.0.0.1", "1.0.0", 1)]
    [InlineData("12", "12.0", -1)]
    [InlineData("12.0", "12", 1)]
    // 空串
    [InlineData("", "", 0)]
    [InlineData("", "1.0.0", -1)]
    [InlineData("1.0.0", "", 1)]
    // 真实 MC 版本
    [InlineData("1.20.1", "1.21.11", -1)]
    [InlineData("1.21.11", "1.20.1", 1)]
    public void CompareVersion_与Node基准一致(string a, string b, int expected)
        => Assert.Equal(expected, DiffEngine.CompareVersion(a, b));

    [Fact]
    public void CompareVersion_大小写与空白_与原版一样只比较数字段()
    {
        // Node 的 normVersion 才做 trim/lowercase，compareVersion 不做
        Assert.Equal(0, DiffEngine.CompareVersion(" 1.0.0 ", "1.0.0"));
    }

    // ==================================================================
    // scanLocalMods
    // ==================================================================

    [Fact]
    public void ScanLocalMods_只收jar与jarDisabled_按原生枚举顺序()
    {
        var dir = DiffFixtures.MakeLocalDir();
        try
        {
            foreach (var n in new[]
                     {
                         "zeta.jar", "alpha.jar", "beta.jar.disabled",
                         "readme.txt", "notes.zip", "config.toml",
                     })
            {
                File.WriteAllBytes(Path.Combine(dir, n), [1, 2, 3]);
            }

            // 【保真】刻意不排序：Node 用 readdirSync 原生顺序，
            // 该顺序会传到变更清单展示顺序，是用户可见行为
            var got = DiffEngine.ScanLocalModsPublic(dir);
            Assert.Equal(3, got.Count);
            Assert.Equal(["alpha.jar", "beta.jar.disabled", "zeta.jar"], got.OrderBy(x => x, StringComparer.Ordinal));

            // 非 jar 一律不收
            Assert.DoesNotContain("readme.txt", got);
            Assert.DoesNotContain("notes.zip", got);
        }
        finally
        {
            DiffFixtures.DeleteDir(dir);
        }
    }

    [Fact]
    public void ScanLocalMods_大小写不敏感_大写JAR也收()
    {
        var dir = DiffFixtures.MakeLocalDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "MOD.JAR"), [1]);
            File.WriteAllBytes(Path.Combine(dir, "X.Jar.Disabled"), [1]);
            Assert.Equal(2, DiffEngine.ScanLocalModsPublic(dir).Count);
        }
        finally
        {
            DiffFixtures.DeleteDir(dir);
        }
    }

    [Fact]
    public void ScanLocalMods_目录不存在_返回空()
        => Assert.Empty(DiffEngine.ScanLocalModsPublic(Path.Combine(Path.GetTempPath(), "不存在-dir-xyz")));

    // ==================================================================
    // diffMods：场景 A（基准来自 Node 实跑）
    // ==================================================================

    private static Dictionary<string, byte[]> ModsA() => new()
    {
        ["alpha.jar"] = DiffFixtures.Jar("alpha", "Alpha Mod", "1.0.0"),
        ["bravo.jar"] = DiffFixtures.Jar("bravo", "Bravo Mod", "2.0.0"),
        ["delta.jar"] = DiffFixtures.Jar("delta", "Delta Mod", "1.0.0"),
    };

    /// <summary>造场景 A 的本地目录：alpha 同版、bravo 旧版、charlie 玩家自装。</summary>
    private static string MakeLocalA()
    {
        var dir = DiffFixtures.MakeLocalDir();
        var mods = Path.Combine(dir, "mods");
        Directory.CreateDirectory(mods);
        File.WriteAllBytes(Path.Combine(mods, "alpha.jar"), ModsA()["alpha.jar"]);
        File.WriteAllBytes(Path.Combine(mods, "bravo.jar"), DiffFixtures.Jar("bravo", "Bravo Mod", "1.0.0"));
        File.WriteAllBytes(Path.Combine(mods, "charlie.jar"), DiffFixtures.Jar("charlie", "Charlie Mod", "1.0.0"));
        return dir;
    }

    private static Manifest ManifestA() => new()
    {
        Mods =
        [
            new ManifestMod { Id = "alpha", FileName = "alpha.jar", Version = "1.0.0" },
            new ManifestMod { Id = "bravo", FileName = "bravo.jar", Version = "1.0.0" },
        ],
    };

    [Fact]
    public void DiffMods_场景A_变更顺序与Node基准完全一致()
    {
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(ModsA()));
        var local = MakeLocalA();
        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffMods(pack.ListCategory("mods"), Path.Combine(local, "mods"),
                pack, ManifestA(), null);

            // Node 实跑：unchanged|alpha → updated|bravo → added|delta → removed|charlie
            Assert.Equal(
                ["unchanged|id:alpha", "updated|id:bravo", "added|id:delta", "removed|id:charlie"],
                r.Changes.Select(c => $"{c.Type}|{c.Key}"));
        }
        finally
        {
            DiffFixtures.DeleteDir(local);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffMods_场景A_各字段与Node基准一致()
    {
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(ModsA()));
        var local = MakeLocalA();
        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffMods(pack.ListCategory("mods"), Path.Combine(local, "mods"),
                pack, ManifestA(), null);
            var byKey = r.Changes.ToDictionary(c => c.Key);

            // --- unchanged: alpha（字节一致） ---
            var same = byKey["id:alpha"];
            Assert.Equal(ChangeType.Unchanged, same.Type);
            Assert.Equal("Alpha Mod", same.DisplayName);
            Assert.Equal("1.0.0", same.OldVersion);
            Assert.Equal("1.0.0", same.NewVersion);
            Assert.False(same.Checked);
            Assert.False(same.Renamed);          // 同名
            Assert.Equal("alpha.jar", same.OldFileName);

            // --- updated: bravo 1.0.0 → 2.0.0 ---
            var updated = byKey["id:bravo"];
            Assert.Equal(ChangeType.Updated, updated.Type);
            Assert.Equal("1.0.0", updated.OldVersion);
            Assert.Equal("2.0.0", updated.NewVersion);
            Assert.True(updated.Checked);
            Assert.Equal(ChangeReason.Version, updated.Reason);
            Assert.False(updated.Downgrade);
            Assert.False(updated.Duplicated);

            // --- added: delta ---
            var added = byKey["id:delta"];
            Assert.Equal(ChangeType.Added, added.Type);
            Assert.Equal("1.0.0", added.NewVersion);
            Assert.Null(added.OldVersion);
            Assert.True(added.Checked);

            // --- removed: charlie（manifest 里没有 → 玩家自装） ---
            var removed = byKey["id:charlie"];
            Assert.Equal(ChangeType.Removed, removed.Type);
            Assert.Equal("1.0.0", removed.OldVersion);
            Assert.Null(removed.NewVersion);
            Assert.True(removed.PlayerInstalled);      // 基准：true
            Assert.False(removed.Checked);             // 玩家自装 → 不勾删除
        }
        finally
        {
            DiffFixtures.DeleteDir(local);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffMods_无manifest时playerInstalled为null_而非false()
    {
        // 【关键】三态语义：null = 无从判断（首次运行）。
        // 若压成 false，checked 会变成 true，首次运行就会把玩家自装的 mod 全删。
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(ModsA()));
        var local = MakeLocalA();
        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffMods(pack.ListCategory("mods"), Path.Combine(local, "mods"),
                pack, null, null);
            var removed = r.Changes.Single(c => c.Key == "id:charlie");

            Assert.Null(removed.PlayerInstalled);
            Assert.False(removed.Checked);   // Node: checked = hasManifest ? !playerInstalled : false
        }
        finally
        {
            DiffFixtures.DeleteDir(local);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffMods_空manifest等价于无manifest()
    {
        // hasManifest 判据是「mods 列表非空」，空列表等同没有
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(ModsA()));
        var local = MakeLocalA();
        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffMods(pack.ListCategory("mods"), Path.Combine(local, "mods"),
                pack, new Manifest { Mods = [] }, null);
            var removed = r.Changes.Single(c => c.Key == "id:charlie");
            Assert.Null(removed.PlayerInstalled);
            Assert.False(removed.Checked);
        }
        finally
        {
            DiffFixtures.DeleteDir(local);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffMods_整合包装的本地mod_移除项默认勾选()
    {
        // manifest 里有的（如 alpha）如果本地有但整合包没有，应该默认勾删除
        var pack = new Dictionary<string, byte[]> { ["delta.jar"] = DiffFixtures.Jar("delta", "Delta", "1.0.0") };
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(pack));
        var local = MakeLocalA();
        try
        {
            using var h = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffMods(h.ListCategory("mods"), Path.Combine(local, "mods"),
                h, ManifestA(), null);

            var alpha = r.Changes.Single(c => c.Key == "id:alpha");
            Assert.Equal(ChangeType.Removed, alpha.Type);
            Assert.False(alpha.PlayerInstalled);   // 在 manifest 里 → 不是玩家自装
            Assert.True(alpha.Checked);             // 整合包装的 → 默认勾删除
        }
        finally
        {
            DiffFixtures.DeleteDir(local);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffMods_场景A无冲突()
    {
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(ModsA()));
        var local = MakeLocalA();
        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffMods(pack.ListCategory("mods"), Path.Combine(local, "mods"),
                pack, ManifestA(), null);
            Assert.Empty(r.Conflicts);
        }
        finally
        {
            DiffFixtures.DeleteDir(local);
            File.Delete(packPath);
        }
    }

    // ==================================================================
    // diffMods：场景 B（本地重复，基准来自 Node 实跑）
    // ==================================================================

    [Fact]
    public void DiffMods_场景B_副本文件被选为代表_与Node基准一致()
    {
        // Node 实跑结果：三份 sodium（0.8.12 / 0.8.13 / 0.9.0-副本）里，
        // pick 选了版本最高的「zzz-sodium.jar - 副本.jar」(0.9.0) 作代表 ——
        // 因为副本标记只在版本相同时才参与比较，版本优先。
        var packMods = new Dictionary<string, byte[]>
        {
            ["sodium-0.8.13.jar"] = DiffFixtures.Jar("sodium", "Sodium", "0.8.13"),
        };
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(packMods));

        var local = DiffFixtures.MakeLocalDir();
        var modsDir = Path.Combine(local, "mods");
        Directory.CreateDirectory(modsDir);
        File.WriteAllBytes(Path.Combine(modsDir, "sodium-0.8.12.jar"), DiffFixtures.Jar("sodium", "Sodium", "0.8.12"));
        File.WriteAllBytes(Path.Combine(modsDir, "sodium-0.8.13.jar"), DiffFixtures.Jar("sodium", "Sodium", "0.8.13"));
        File.WriteAllBytes(Path.Combine(modsDir, "zzz-sodium.jar - 副本.jar"), DiffFixtures.Jar("sodium", "Sodium", "0.9.0"));

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffMods(pack.ListCategory("mods"), modsDir, pack, null, null);

            // Node 基准顺序：updated|id:sodium → 两个 dup 移除项
            Assert.Equal(
                [
                    "updated|id:sodium|sodium-0.8.13.jar",
                    "removed|dup::id:sodium::sodium-0.8.12.jar|sodium-0.8.12.jar",
                    "removed|dup::id:sodium::sodium-0.8.13.jar|sodium-0.8.13.jar",
                ],
                r.Changes.Select(c => $"{c.Type}|{c.Key}|{c.FileName}"));

            var main = r.Changes[0];
            Assert.Equal("0.9.0", main.OldVersion);      // 代表项是 0.9.0 的副本
            Assert.Equal("0.8.13", main.NewVersion);
            Assert.Equal(ChangeReason.Downgrade, main.Reason);  // 0.8.13 < 0.9.0
            Assert.True(main.Downgrade);

            foreach (var dup in r.Changes.Skip(1))
            {
                Assert.Equal(ChangeType.Removed, dup.Type);
                Assert.True(dup.Duplicate);
                Assert.Equal("zzz-sodium.jar - 副本.jar", dup.DuplicateOf);
                Assert.Equal("0.9.0", dup.DuplicateOfVersion);
                Assert.EndsWith("（重复）", dup.DisplayName);
                Assert.False(dup.Checked);        // 不自动勾
                Assert.Null(dup.PlayerInstalled);  // 重复项无法判断归属
            }

            // 冲突告警：三份都在列表里
            var conflict = Assert.Single(r.Conflicts);
            Assert.Equal("local", conflict.Side);
            Assert.Equal("id:sodium", conflict.Key);
            Assert.Equal("Sodium", conflict.DisplayName);
            Assert.Equal(3, conflict.Files.Count);
            Assert.Equal(
                ["sodium-0.8.12.jar", "sodium-0.8.13.jar", "zzz-sodium.jar - 副本.jar"],
                conflict.Files.Select(f => f.FileName));
        }
        finally
        {
            DiffFixtures.DeleteDir(local);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffMods_同版本时非副本优先_副本进移除列表()
    {
        // 版本相同时，副本标记才起作用：原版当代表，副本被列为多余
        var packMods = new Dictionary<string, byte[]>
        {
            ["sodium.jar"] = DiffFixtures.Jar("sodium", "Sodium", "0.8.13"),
        };
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(packMods));

        var local = DiffFixtures.MakeLocalDir();
        var modsDir = Path.Combine(local, "mods");
        Directory.CreateDirectory(modsDir);
        File.WriteAllBytes(Path.Combine(modsDir, "sodium.jar"), DiffFixtures.Jar("sodium", "Sodium", "0.8.13"));
        File.WriteAllBytes(Path.Combine(modsDir, "sodium.jar - 副本.jar"), DiffFixtures.Jar("sodium", "Sodium", "0.8.13"));

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffMods(pack.ListCategory("mods"), modsDir, pack, null, null);

            // 代表项是原版 sodium.jar（unchanged），副本进移除列表
            Assert.Equal(ChangeType.Unchanged, r.Changes[0].Type);
            var dup = r.Changes.Single(c => c.Duplicate == true);
            Assert.Equal("sodium.jar - 副本.jar", dup.FileName);
            Assert.Equal("sodium.jar", dup.DuplicateOf);
        }
        finally
        {
            DiffFixtures.DeleteDir(local);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffMods_zip侧重复只告警不产出移除项()
    {
        // 服主打包事故：删了本地下次更新还会装回来，所以只走 conflicts
        var packMods = new Dictionary<string, byte[]>
        {
            ["sodium-0.8.12.jar"] = DiffFixtures.Jar("sodium", "Sodium", "0.8.12"),
            ["sodium-0.8.13.jar"] = DiffFixtures.Jar("sodium", "Sodium", "0.8.13"),
        };
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(packMods));

        var local = DiffFixtures.MakeLocalDir();
        Directory.CreateDirectory(Path.Combine(local, "mods"));

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffMods(pack.ListCategory("mods"), Path.Combine(local, "mods"),
                pack, null, null);

            var conflict = Assert.Single(r.Conflicts);
            Assert.Equal("zip", conflict.Side);
            // 主变更只有一条（added），没有任何 duplicate 移除项
            Assert.DoesNotContain(r.Changes, c => c.Duplicate == true);
        }
        finally
        {
            DiffFixtures.DeleteDir(local);
            File.Delete(packPath);
        }
    }

    // ==================================================================
    // diffMods：场景 C（内容一致仅改名）
    // ==================================================================

    [Fact]
    public void DiffMods_场景C_内容一致仅改名_判unchanged并标记renamed()
    {
        // PCL2 给 mod 加中文前缀的场景：字节完全相同，只有文件名不同。
        // 若按文件名匹配，会误判成「移除旧的 + 新增新的」。
        var bytes = DiffFixtures.Jar("sodium", "Sodium", "0.8.13");
        var packMods = new Dictionary<string, byte[]> { ["[钠] sodium-fabric-0.8.13.jar"] = bytes };
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(packMods));

        var local = DiffFixtures.MakeLocalDir();
        var modsDir = Path.Combine(local, "mods");
        Directory.CreateDirectory(modsDir);
        File.WriteAllBytes(Path.Combine(modsDir, "sodium-fabric-0.8.13.jar"), bytes);

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffMods(pack.ListCategory("mods"), modsDir, pack, null, null);

            var c = Assert.Single(r.Changes);
            Assert.Equal(ChangeType.Unchanged, c.Type);
            Assert.Equal("[钠] sodium-fabric-0.8.13.jar", c.FileName);
            Assert.Equal("sodium-fabric-0.8.13.jar", c.OldFileName);
            Assert.True(c.Renamed);
            Assert.Equal("0.8.13", c.NewVersion);
            Assert.False(c.Checked);
        }
        finally
        {
            DiffFixtures.DeleteDir(local);
            File.Delete(packPath);
        }
    }

    // ==================================================================
    // diffMods：场景 D（同版本内容不同）
    // ==================================================================

    [Fact]
    public void DiffMods_场景D_同版本内容不同_理由为content()
    {
        var packMods = new Dictionary<string, byte[]> { ["echo.jar"] = DiffFixtures.Jar("echo", "Echo Mod", "1.0.0") };
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(packMods));

        var local = DiffFixtures.MakeLocalDir();
        var modsDir = Path.Combine(local, "mods");
        Directory.CreateDirectory(modsDir);
        // 版本号一样，但 name 不同 → jar 字节不同 → 内容变更
        File.WriteAllBytes(Path.Combine(modsDir, "echo.jar"), DiffFixtures.Jar("echo", "Echo Mod Patched", "1.0.0"));

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffMods(pack.ListCategory("mods"), modsDir, pack, null, null);

            var c = Assert.Single(r.Changes);
            Assert.Equal(ChangeType.Updated, c.Type);
            Assert.Equal("1.0.0", c.OldVersion);
            Assert.Equal("1.0.0", c.NewVersion);
            Assert.Equal(ChangeReason.Content, c.Reason);   // 不是 version
            Assert.False(c.Downgrade);
            Assert.True(c.Checked);
        }
        finally
        {
            DiffFixtures.DeleteDir(local);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffMods_降级场景_标记downgrade()
    {
        var packMods = new Dictionary<string, byte[]> { ["echo.jar"] = DiffFixtures.Jar("echo", "Echo", "1.0.0") };
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(packMods));

        var local = DiffFixtures.MakeLocalDir();
        var modsDir = Path.Combine(local, "mods");
        Directory.CreateDirectory(modsDir);
        File.WriteAllBytes(Path.Combine(modsDir, "echo.jar"), DiffFixtures.Jar("echo", "Echo", "2.0.0"));

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffMods(pack.ListCategory("mods"), modsDir, pack, null, null);

            var c = Assert.Single(r.Changes);
            Assert.Equal(ChangeType.Updated, c.Type);
            Assert.Equal("2.0.0", c.OldVersion);
            Assert.Equal("1.0.0", c.NewVersion);
            Assert.Equal(ChangeReason.Downgrade, c.Reason);
            Assert.True(c.Downgrade);
        }
        finally
        {
            DiffFixtures.DeleteDir(local);
            File.Delete(packPath);
        }
    }

    // ==================================================================
    // diffMods：guessed 兜底
    // ==================================================================

    [Fact]
    public void DiffMods_解析不出id的jar_按文件名匹配且guessed为true()
    {
        // 非标准 jar（没有 fabric.mod.json）→ 走 ParseFromFileName 兜底
        var bogus = DiffFixtures.StoredZip(("readme.txt", System.Text.Encoding.UTF8.GetBytes("not a real mod")));
        var packMods = new Dictionary<string, byte[]> { ["coolmod-1.2.3.jar"] = bogus };
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(packMods));

        var local = DiffFixtures.MakeLocalDir();
        Directory.CreateDirectory(Path.Combine(local, "mods"));

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffMods(pack.ListCategory("mods"), Path.Combine(local, "mods"),
                pack, null, null);

            var c = Assert.Single(r.Changes);
            Assert.Equal(ChangeType.Added, c.Type);
            // key 用 file: 前缀（guessed 兜底），不是 id:
            Assert.StartsWith("file:", c.Key);
            Assert.Equal("coolmod-1.2.3.jar", c.FileName);
        }
        finally
        {
            DiffFixtures.DeleteDir(local);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffMods_非jar文件被过滤掉()
    {
        var packMods = new Dictionary<string, byte[]>
        {
            ["a.jar"] = DiffFixtures.Jar("a", "A", "1.0.0"),
        };
        // extra 的路径已经是完整 zip 内路径（Mrpack 不再给它加前缀）
        var extra = new (string, byte[])[] { ("overrides/mods/readme.txt", System.Text.Encoding.UTF8.GetBytes("hi")) };
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(packMods, extra: extra));

        var local = DiffFixtures.MakeLocalDir();
        Directory.CreateDirectory(Path.Combine(local, "mods"));
        try
        {
            using var pack = ModpackReader.Open(packPath);
            // 分类里有 2 个（a.jar + readme.txt）—— listCategory 不做类型过滤
            Assert.Equal(2, pack.ListCategory("mods").Count);
            var r = DiffEngine.DiffMods(pack.ListCategory("mods"), Path.Combine(local, "mods"),
                pack, null, null);
            // 但 diff 只处理 .jar / .jar.disabled
            Assert.Single(r.Changes);
            Assert.Equal("a.jar", r.Changes[0].FileName);
        }
        finally
        {
            DiffFixtures.DeleteDir(local);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffMods_本地mods目录不存在_全部判为新增()
    {
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(ModsA()));
        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffMods(pack.ListCategory("mods"),
                Path.Combine(Path.GetTempPath(), "不存在的目录-abc", "mods"), pack, null, null);

            Assert.Equal(3, r.Changes.Count);
            Assert.All(r.Changes, c => Assert.Equal(ChangeType.Added, c.Type));
        }
        finally
        {
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffMods_进度回调_走到70百分比()
    {
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(ModsA()));
        var local = MakeLocalA();
        try
        {
            var seen = new List<int>();
            using var pack = ModpackReader.Open(packPath);
            _ = DiffEngine.DiffMods(pack.ListCategory("mods"), Path.Combine(local, "mods"),
                pack, null, null, (_, pct) => seen.Add(pct));

            Assert.Equal([45, 55, 70], seen);
        }
        finally
        {
            DiffFixtures.DeleteDir(local);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffMods_jarDisabled_视为存在且标记disabled()
    {
        var packMods = new Dictionary<string, byte[]> { ["alpha.jar"] = DiffFixtures.Jar("alpha", "Alpha", "1.0.0") };
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(packMods));

        var local = DiffFixtures.MakeLocalDir();
        var modsDir = Path.Combine(local, "mods");
        Directory.CreateDirectory(modsDir);
        // 整合包里有 alpha，本地只有被禁用的 alpha
        File.WriteAllBytes(Path.Combine(modsDir, "alpha.jar.disabled"),
            DiffFixtures.Jar("alpha", "Alpha", "0.9.0"));

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffMods(pack.ListCategory("mods"), modsDir, pack, null, null);

            var c = Assert.Single(r.Changes);
            Assert.Equal(ChangeType.Updated, c.Type);
            // 【保真】updated 的 fileName 取 zip 侧（Node: nameOf(z.item)），
            // 本地的 .disabled 名只体现在 oldFileName 上
            Assert.Equal("alpha.jar", c.FileName);
            Assert.Equal("alpha.jar.disabled", c.OldFileName);
            // 【保真】Node 的 disabled 字段只出现在 removed 与重复项两处 push 里，
            // updated 分支根本没写这个键 —— 所以这里必须是 null，不能"顺手补上"
            Assert.Null(c.Disabled);
        }
        finally
        {
            DiffFixtures.DeleteDir(local);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffMods_移除的disabled文件_带disabled标记()
    {
        // 与上一条对照：removed 分支确实写 disabled（Node 第 365 行）
        var packMods = new Dictionary<string, byte[]>();
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(packMods));

        var local = DiffFixtures.MakeLocalDir();
        var modsDir = Path.Combine(local, "mods");
        Directory.CreateDirectory(modsDir);
        File.WriteAllBytes(Path.Combine(modsDir, "orphan.jar.disabled"),
            DiffFixtures.Jar("orphan", "Orphan", "1.0.0"));

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffMods(pack.ListCategory("mods"), modsDir, pack, null, null);

            var c = Assert.Single(r.Changes);
            Assert.Equal(ChangeType.Removed, c.Type);
            Assert.Equal("orphan.jar.disabled", c.FileName);
            Assert.True(c.Disabled);
        }
        finally
        {
            DiffFixtures.DeleteDir(local);
            File.Delete(packPath);
        }
    }

    // ==================================================================
    // diffFiles：config / resourcepacks / shaderpacks
    // ==================================================================

    /// <summary>
    /// 造一个 config 分类的对比场景。
    /// </summary>
    /// <remarks>
    /// <b>本地目录直接就是 config 目录本身</b>（不是它的父目录）——
    /// <c>DiffFiles</c> 拿到的 <c>localDir</c> 语义是「该分类在目标版本目录下的位置」，
    /// 里面就是 <c>a.toml</c>、<c>sub/b.toml</c>，没有 <c>config/</c> 这一层。
    /// 多套一层会让 <c>ListFilesRecursive</c> 返回 <c>config/z_local.toml</c>，
    /// 与 zip 侧的 <c>z_local.toml</c> 对不上，凭空多出一条"本地多余"。
    /// </remarks>
    private static (string PackPath, string LocalDir) MakeFileCase(
        params (string RelPath, string Content)[] zipFiles)
    {
        var extra = zipFiles
            .Select(f => ($"overrides/config/{f.RelPath}", System.Text.Encoding.UTF8.GetBytes(f.Content)))
            .ToArray();
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack([], extra: extra));

        var localDir = DiffFixtures.MakeLocalDir();
        return (packPath, localDir);
    }

    [Fact]
    public void DiffFiles_新增文件_默认勾选()
    {
        var (packPath, localDir) = MakeFileCase(("new.toml", "a=1"));
        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffFiles("config", pack.ListCategory("config"), localDir, null);

            var c = Assert.Single(r.Changes);
            Assert.Equal(ChangeType.Added, c.Type);
            Assert.Equal("new.toml", c.Key);       // key 是小写相对路径
            Assert.Equal("new.toml", c.FileName);
            Assert.True(c.Checked);
            Assert.Equal("new.toml", c.ZipRelPath);
        }
        finally
        {
            DiffFixtures.DeleteDir(localDir);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffFiles_配置内容不同_默认不勾选()
    {
        // 【重要】配置玩家常自己调参，覆盖会丢设置 —— updated 也不勾
        var (packPath, localDir) = MakeFileCase(("a.toml", "new"));
        File.WriteAllText(Path.Combine(localDir, "a.toml"), "old");

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffFiles("config", pack.ListCategory("config"), localDir, null);

            var c = Assert.Single(r.Changes);
            Assert.Equal(ChangeType.Updated, c.Type);
            Assert.Equal(ChangeReason.Content, c.Reason);
            Assert.False(c.Checked);
        }
        finally
        {
            DiffFixtures.DeleteDir(localDir);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffFiles_内容一致_判unchanged()
    {
        var (packPath, localDir) = MakeFileCase(("a.toml", "same"));
        File.WriteAllText(Path.Combine(localDir, "a.toml"), "same");

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffFiles("config", pack.ListCategory("config"), localDir, null);

            var c = Assert.Single(r.Changes);
            Assert.Equal(ChangeType.Unchanged, c.Type);
            Assert.False(c.Checked);
        }
        finally
        {
            DiffFixtures.DeleteDir(localDir);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffFiles_本地多余文件_移除项永不自动勾选()
    {
        var (packPath, localDir) = MakeFileCase();
        File.WriteAllText(Path.Combine(localDir, "extra.toml"), "x");

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffFiles("config", pack.ListCategory("config"), localDir, null);

            var c = Assert.Single(r.Changes);
            Assert.Equal(ChangeType.Removed, c.Type);
            Assert.False(c.Checked);
            Assert.Null(c.PlayerInstalled);      // 无该分类记录 → 无从判断
        }
        finally
        {
            DiffFixtures.DeleteDir(localDir);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffFiles_有分类记录时区分玩家自装()
    {
        var (packPath, localDir) = MakeFileCase();
        File.WriteAllText(Path.Combine(localDir, "kept.toml"), "k");    // 记录里有 → 整合包装的
        File.WriteAllText(Path.Combine(localDir, "mine.toml"), "m");     // 记录里没有 → 玩家自装

        var manifest = new Manifest
        {
            Files = new Dictionary<string, List<ManifestFile>>
            {
                ["config"] = [new ManifestFile { Path = "kept.toml" }],
            },
        };

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffFiles("config", pack.ListCategory("config"), localDir, manifest);

            var kept = r.Changes.Single(c => c.FileName == "kept.toml");
            var mine = r.Changes.Single(c => c.FileName == "mine.toml");
            Assert.False(kept.PlayerInstalled);
            Assert.True(mine.PlayerInstalled);
        }
        finally
        {
            DiffFixtures.DeleteDir(localDir);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffFiles_保护规则_备份与缓存类文件不参与对比()
    {
        // fuji/backup/** 与 **/*.zip 是保护规则命中的典型场景
        var extra = new (string, byte[])[]
        {
            ("overrides/config/fuji/backup/world-1.zip", [1, 2, 3]),
            ("overrides/config/fuji/cache/x.dat", [1, 2, 3]),
            ("overrides/config/jei/world/服务器/1.json", [1, 2, 3]),
            ("overrides/config/voicechat/config.json", [1, 2, 3]),
            ("overrides/config/normal.toml", [1]),
        };
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack([], extra: extra));
        var localDir = DiffFixtures.MakeLocalDir();

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffFiles("config", pack.ListCategory("config"), localDir, null);

            var c = Assert.Single(r.Changes);
            Assert.Equal("normal.toml", c.FileName);
            Assert.NotNull(r.Skipped);
            Assert.Equal(4, r.Skipped!.Protected);
        }
        finally
        {
            DiffFixtures.DeleteDir(localDir);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffFiles_路径穿越条目被拒()
    {
        var extra = new (string, byte[])[]
        {
            ("overrides/config/../../evil.txt", [1]),
        };
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack([], extra: extra));
        var localDir = DiffFixtures.MakeLocalDir();

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffFiles("config", pack.ListCategory("config"), localDir, null);
            Assert.Empty(r.Changes);
            Assert.Equal(1, r.Skipped!.Protected);
        }
        finally
        {
            DiffFixtures.DeleteDir(localDir);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffFiles_超大配置文件被跳过()
    {
        // config 单文件上限 8MB；本地侧的也会再挡一道（不给删）
        var big = new byte[AppConfig.ConfigMaxFile + 1];
        var extra = new (string, byte[])[] { ("overrides/config/big.dat", big) };
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack([], extra: extra));

        var localDir = DiffFixtures.MakeLocalDir();
        File.WriteAllBytes(Path.Combine(localDir, "big.dat"), big);

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffFiles("config", pack.ListCategory("config"), localDir, null);
            Assert.Empty(r.Changes);
            Assert.True(r.Skipped!.Oversize >= 1);
        }
        finally
        {
            DiffFixtures.DeleteDir(localDir);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffFiles_资源包只认zip_说明文档被跳过()
    {
        var extra = new (string, byte[])[]
        {
            ("overrides/resourcepacks/pack.zip", [1, 2]),
            ("overrides/resourcepacks/readme.txt", [1]),
            ("overrides/resourcepacks/old.zip.disabled", [1]),
        };
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack([], extra: extra));
        var localDir = DiffFixtures.MakeLocalDir();

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffFiles("resourcepacks", pack.ListCategory("resourcepacks"), localDir, null);

            var c = Assert.Single(r.Changes);
            Assert.Equal("pack.zip", c.FileName);
            // readme.txt（txt）与 .disabled 都被扩展名过滤挡掉
            Assert.Equal(2, r.Skipped!.Ext);
        }
        finally
        {
            DiffFixtures.DeleteDir(localDir);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffFiles_资源包内容变更_默认勾选_与config不同()
    {
        // config 不勾（怕丢玩家调参），但资源包可以放心覆盖
        var extra = new (string, byte[])[] { ("overrides/resourcepacks/pack.zip", [1, 2, 3]) };
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack([], extra: extra));

        var localDir = DiffFixtures.MakeLocalDir();
        Directory.CreateDirectory(localDir); File.WriteAllBytes(Path.Combine(localDir, "pack.zip"), [9, 9]);

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffFiles("resourcepacks", pack.ListCategory("resourcepacks"), localDir, null);

            var c = Assert.Single(r.Changes);
            Assert.Equal(ChangeType.Updated, c.Type);
            Assert.True(c.Checked);
        }
        finally
        {
            DiffFixtures.DeleteDir(localDir);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffFiles_子目录文件_递归列出并保留相对路径()
    {
        var extra = new (string, byte[])[]
        {
            ("overrides/config/sub/deep/a.toml", [1]),
            ("overrides/config/sub/deep/b.toml", [2]),
        };
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack([], extra: extra));
        var localDir = DiffFixtures.MakeLocalDir();

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffFiles("config", pack.ListCategory("config"), localDir, null);
            Assert.Equal(2, r.Changes.Count);
            Assert.Contains(r.Changes, c => c.FileName == "sub/deep/a.toml");
        }
        finally
        {
            DiffFixtures.DeleteDir(localDir);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffFiles_大小写不同视为同一文件()
    {
        var (packPath, localDir) = MakeFileCase(("Config.toml", "x"));
        File.WriteAllText(Path.Combine(localDir, "config.toml"), "x");

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffFiles("config", pack.ListCategory("config"), localDir, null);
            // key 全小写 → 只有一条，且是 unchanged
            var c = Assert.Single(r.Changes);
            Assert.Equal(ChangeType.Unchanged, c.Type);
            Assert.Equal("config.toml", c.Key);
        }
        finally
        {
            DiffFixtures.DeleteDir(localDir);
            File.Delete(packPath);
        }
    }

    [Fact]
    public void DiffFiles_顺序为zip侧插入序再本地独有()
    {
        var (packPath, localDir) = MakeFileCase(("b.toml", "1"), ("a.toml", "2"));
        File.WriteAllText(Path.Combine(localDir, "z_local.toml"), "3");

        try
        {
            using var pack = ModpackReader.Open(packPath);
            var r = DiffEngine.DiffFiles("config", pack.ListCategory("config"), localDir, null);
            Assert.Equal(["b.toml", "a.toml", "z_local.toml"], r.Changes.Select(c => c.Key));
        }
        finally
        {
            DiffFixtures.DeleteDir(localDir);
            File.Delete(packPath);
        }
    }

    // ==================================================================
    // MetaCache
    // ==================================================================

    [Fact]
    public void MetaCache_无文件时为空且Get返回null()
    {
        var cache = new MetaCache(Path.Combine(Path.GetTempPath(), "不存在的缓存-abc.json"));
        Assert.Null(cache.Get("123:456"));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void MetaCache_Set后能命中()
    {
        var cache = new MetaCache();
        cache.Set("100:200", new ModInfo
        {
            Id = "x", Name = "X", Version = "1.0", Loader = "fabric", Guessed = false, FileName = "x.jar",
        });
        var got = cache.Get("100:200");
        Assert.NotNull(got);
        Assert.Equal("x", got!.Id);
        Assert.Equal("fabric", got.Loader);
    }

    [Fact]
    public void MetaCache_落盘后重新加载_内容一致()
    {
        var dir = DiffFixtures.MakeLocalDir();
        var path = Path.Combine(dir, "cache.json");
        try
        {
            var c1 = new MetaCache(path);
            c1.Set("100:200", new ModInfo
            {
                Id = "sodium", Name = "Sodium", Version = "0.8.13", Loader = "fabric",
                Guessed = false, FileName = "sodium.jar",
            });
            Assert.True(c1.Save());

            var c2 = new MetaCache(path);
            var got = c2.Get("100:200");
            Assert.NotNull(got);
            Assert.Equal("sodium", got!.Id);
            Assert.Equal("Sodium", got.Name);
            Assert.False(got.Guessed);
        }
        finally
        {
            DiffFixtures.DeleteDir(dir);
        }
    }

    [Fact]
    public void MetaCache_缓存文件损坏_当没有处理()
    {
        var dir = DiffFixtures.MakeLocalDir();
        var path = Path.Combine(dir, "cache.json");
        File.WriteAllText(path, "{ 这不是合法 JSON");
        try
        {
            var cache = new MetaCache(path);
            Assert.Equal(0, cache.Count);
            Assert.Null(cache.Get("anything"));
        }
        finally
        {
            DiffFixtures.DeleteDir(dir);
        }
    }

    [Fact]
    public void DiffMods_传入MetaCache_第二次不再重复解析()
    {
        var packPath = DiffFixtures.WritePack(DiffFixtures.Mrpack(ModsA()));
        var local = MakeLocalA();
        try
        {
            var cache = new MetaCache();
            using var pack = ModpackReader.Open(packPath);
            var zipMods = pack.ListCategory("mods");

            _ = DiffEngine.DiffMods(zipMods, Path.Combine(local, "mods"), pack, ManifestA(), cache);
            var afterFirst = cache.Count;
            Assert.True(afterFirst >= 3);

            // 第二次结果必须完全一致（缓存不能改变行为）
            var r2 = DiffEngine.DiffMods(zipMods, Path.Combine(local, "mods"), pack, ManifestA(), cache);
            Assert.Equal(
                ["unchanged|id:alpha", "updated|id:bravo", "added|id:delta", "removed|id:charlie"],
                r2.Changes.Select(c => $"{c.Type}|{c.Key}"));
        }
        finally
        {
            DiffFixtures.DeleteDir(local);
            File.Delete(packPath);
        }
    }

    // ==================================================================
    // 保护规则匹配器
    // ==================================================================

    [Fact]
    public void GetProtectedMatcher_mods分类无规则_恒不命中()
    {
        var m = DiffEngine.GetProtectedMatcher("mods");
        Assert.False(m("anything.jar"));
        Assert.False(m("a/b/c.txt"));
    }

    [Fact]
    public void GetProtectedMatcher_config规则生效()
    {
        var m = DiffEngine.GetProtectedMatcher("config");
        Assert.True(m("fuji/backup/world.zip"));
        Assert.True(m("jei/world/服务器/1.json"));
        Assert.True(m("voicechat/config.json"));
        Assert.True(m("backup.zip"));          // **/*.zip 的零层语义
        Assert.False(m("normal.toml"));
    }

    [Fact]
    public void GetProtectedMatcher_重复调用返回缓存同一实例()
    {
        var a = DiffEngine.GetProtectedMatcher("config");
        var b = DiffEngine.GetProtectedMatcher("config");
        Assert.Same(a, b);   // Node 用 Map 缓存编译结果，避免每次重编译正则
    }
}
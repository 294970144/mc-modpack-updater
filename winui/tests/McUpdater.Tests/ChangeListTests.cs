using McUpdater.Core;

namespace McUpdater.Tests;

/// <summary>
/// M5：<see cref="ChangeList"/> 的单元测试（合成数据，不碰真实目录）。
///
/// <para>这一层的规则密度比看上去高：七个标签各自的触发条件与顺序、
/// <c>duplicate</c> 与 <c>duplicated</c> 的互斥、版本号的四种呈现、
/// 「全部」视图藏掉无变化项。规则错了界面不会崩，只会<em>安静地误导</em> ——
/// 所以它必须能被断言，不能只靠看。</para>
/// </summary>
public class ChangeListTests
{
    private static ChangeItem Item(
        string type,
        string key = "k1",
        string category = "mods",
        string display = "某个模组",
        string file = "a.jar",
        long size = 1024,
        bool? checked_ = true,
        bool? playerInstalled = null,
        bool? downgrade = null,
        string? reason = null,
        bool? duplicate = null,
        string? duplicateOf = null,
        bool? duplicated = null,
        bool? disabled = null,
        bool? renamed = null,
        string? oldVersion = null,
        string? newVersion = null)
        => new()
        {
            Type = type,
            Category = category,
            Key = key,
            DisplayName = display,
            FileName = file,
            Size = size,
            Checked = checked_ ?? false,
            PlayerInstalled = playerInstalled,
            Downgrade = downgrade,
            Reason = reason,
            Duplicate = duplicate,
            DuplicateOf = duplicateOf,
            Duplicated = duplicated,
            Disabled = disabled,
            Renamed = renamed,
            OldVersion = oldVersion,
            NewVersion = newVersion,
        };

    [Fact]
    public void SelKey_带分类前缀()
    {
        Assert.Equal("mods::id:x", ChangeList.SelKey(Item(ChangeType.Added, key: "id:x")));
    }

    [Fact]
    public void BuildRows_标签顺序固定且七条规则各自生效()
    {
        var row = ChangeList.BuildRows([
            Item(ChangeType.Updated, playerInstalled: true, downgrade: true,
                 reason: ChangeReason.Content, disabled: true, renamed: true,
                 oldVersion: "1.0", newVersion: "0.9"),
        ])[0];

        Assert.Equal(["你装的", "降级", "版本号没变但内容变了", "已禁用", "仅文件名不同"],
            row.Tags.Select(t => t.Text));
    }

    [Fact]
    public void BuildRows_duplicate与duplicated互斥()
    {
        // 本地多余项：说清"保留哪一份"，带 tooltip
        var dup = ChangeList.BuildRows([
            Item(ChangeType.Removed, duplicate: true, duplicateOf: "some-mod-1.0.jar",
                 duplicated: true),
        ])[0];
        Assert.Single(dup.Tags, t => t.Text.StartsWith("重复·保留"));
        Assert.DoesNotContain(dup.Tags, t => t.Text == "重复");
        Assert.NotNull(dup.Tags[0].Tooltip);

        // 普通重复：只标红，没有 tooltip
        var plain = ChangeList.BuildRows([
            Item(ChangeType.Removed, duplicated: true),
        ])[0];
        Assert.Equal(["重复"], plain.Tags.Select(t => t.Text));
        Assert.Null(plain.Tags[0].Tooltip);
    }

    [Fact]
    public void BuildRows_版本号的四种呈现()
    {
        var rows = ChangeList.BuildRows([
            Item(ChangeType.Updated, key: "u", oldVersion: "1.0", newVersion: "2.0"),
            Item(ChangeType.Added, key: "a", newVersion: "2.0"),
            Item(ChangeType.Removed, key: "r", oldVersion: "1.0"),
            Item(ChangeType.Unchanged, key: "n", newVersion: "2.0"),
        ]);

        Assert.Equal(("1.0", "2.0"), (rows[0].OldVersion, rows[0].NewVersion));
        Assert.Equal((null, "2.0"), (rows[1].OldVersion, rows[1].NewVersion));
        Assert.Equal(("1.0", null), (rows[2].OldVersion, rows[2].NewVersion));
        Assert.Equal((null, "2.0"), (rows[3].OldVersion, rows[3].NewVersion));
    }

    [Fact]
    public void BuildRows_零字节不显示体积()
    {
        var rows = ChangeList.BuildRows([
            Item(ChangeType.Added, key: "a", size: 0),
            Item(ChangeType.Added, key: "b", size: 2048),
        ]);
        Assert.Equal("", rows[0].SizeText);
        Assert.Equal("2.0 KB", rows[1].SizeText);
    }

    [Fact]
    public void BuildRows_无变化项不可勾选()
    {
        var rows = ChangeList.BuildRows([
            Item(ChangeType.Added, key: "a"),
            Item(ChangeType.Unchanged, key: "n"),
        ]);
        Assert.True(rows[0].CanCheck);
        Assert.False(rows[1].CanCheck);
    }

    [Fact]
    public void Visible_全部视图藏掉无变化()
    {
        var rows = ChangeList.BuildRows([
            Item(ChangeType.Added, key: "a"),
            Item(ChangeType.Unchanged, key: "n"),
        ]);

        Assert.Single(ChangeList.Visible(rows, "all", null));
        // 但点「无变化」筛选项时要能单独看到
        Assert.Single(ChangeList.Visible(rows, "unchanged", null));
        Assert.Single(ChangeList.Visible(rows, "added", null));
        Assert.Empty(ChangeList.Visible(rows, "removed", null));
    }

    [Fact]
    public void Visible_搜索命中名称或文件名且不分大小写()
    {
        var rows = ChangeList.BuildRows([
            Item(ChangeType.Added, key: "a", display: "JourneyMap", file: "journeymap.jar"),
            Item(ChangeType.Added, key: "b", display: "另一个", file: "other.jar"),
        ]);

        Assert.Single(ChangeList.Visible(rows, "all", "journey"));   // 命中 displayName
        Assert.Single(ChangeList.Visible(rows, "all", "OTHER.JAR"));  // 命中 fileName 且忽略大小写
        Assert.Equal(2, ChangeList.Visible(rows, "all", "  ").Count); // 空查询 = 不过滤
        Assert.Empty(ChangeList.Visible(rows, "all", "没有这个"));
    }

    [Fact]
    public void Group_顺序取配置顺序且只含有内容的分类()
    {
        var rows = ChangeList.BuildRows([
            Item(ChangeType.Added, key: "a", category: "shaderpacks"),
            Item(ChangeType.Added, key: "b", category: "mods"),
        ]);

        var groups = ChangeList.Group(rows);
        Assert.Equal(["mods", "shaderpacks"], groups.Select(g => g.Key));
        Assert.Equal("模组", groups[0].Title);
    }

    [Fact]
    public void Group_全是不可勾选项时标记为不可全选()
    {
        var groups = ChangeList.Group(ChangeList.BuildRows([
            Item(ChangeType.Unchanged, key: "n"),
        ]));
        Assert.False(Assert.Single(groups).HasSelectable);
    }

    [Fact]
    public void RecommendedKeys_只收非无变化且checked的()
    {
        ChangeItem[] changes =
        [
            Item(ChangeType.Added, key: "a", checked_: true),
            Item(ChangeType.Updated, key: "b", checked_: false),
            Item(ChangeType.Unchanged, key: "n", checked_: true),   // 勾了也不收
        ];

        Assert.Equal(["mods::a"], ChangeList.RecommendedKeys(changes).OrderBy(x => x));
    }

    [Fact]
    public void Status_只统计勾选中的项()
    {
        ChangeItem[] changes =
        [
            Item(ChangeType.Added, key: "a", size: 100),
            Item(ChangeType.Updated, key: "b", size: 200),
            Item(ChangeType.Removed, key: "c", size: 300),
            Item(ChangeType.Added, key: "d", size: 400),
        ];

        var st = ChangeList.Status(changes, new HashSet<string> { "mods::a", "mods::c", "mods::d" });
        Assert.Equal(3, st.Count);
        Assert.Equal(2, st.Added);
        Assert.Equal(0, st.Updated);
        Assert.Equal(1, st.Removed);
        Assert.Equal(Format.Bytes(800), st.BytesText);
    }

    [Fact]
    public void Status_一个都没勾时返回零()
    {
        var st = ChangeList.Status([Item(ChangeType.Added)], new HashSet<string>());
        Assert.Equal(0, st.Count);
        Assert.Equal("0 B", st.BytesText);
    }
}

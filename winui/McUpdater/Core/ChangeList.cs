namespace McUpdater.Core;

/// <summary>变更行上的一个标签（Node <c>rowHtml</c> 里的 <c>span.tag</c>）。</summary>
/// <param name="Text">标签文案。</param>
/// <param name="StyleKey">样式资源键（对应 CSS 的 tag-amber / tag-red / tag-blue / tag-purple / 基态）。</param>
/// <param name="Tooltip">鼠标悬停说明；仅"重复·保留 X"这条有。</param>
public sealed record ChangeTagView(string Text, string StyleKey, string? Tooltip = null);

/// <summary>
/// 变更列表里的一行（Node <c>rowHtml</c> 的展示模型）。
///
/// <para><b>为什么放在 Core 而不是 Services</b>：这一层的规则密度比想象的
/// 高 —— 七个标签各自的触发条件与顺序、版本号的四种呈现、空 size 不显示。
/// 这些写进 MainWindow 就只能靠"看界面"，写进 Core 才能被测试台直接断言。
/// 与 <see cref="Format"/>（同为展示格式化却放在 Core）同一个道理。</para>
/// </summary>
public sealed record ChangeRowView(
    ChangeItem Item,
    string SelKey,
    string DisplayName,
    string FileName,
    string SizeText,
    string? OldVersion,
    string? NewVersion,
    IReadOnlyList<ChangeTagView> Tags,
    bool CanCheck);

/// <summary>按分类分出的一组（Node <c>renderChanges</c> 的 <c>group-head</c>）。</summary>
public sealed record ChangeGroupView(
    string Key,
    string Title,
    IReadOnlyList<ChangeRowView> Rows,
    bool HasSelectable);

/// <summary>底部状态栏的统计（Node <c>renderStatus</c>）。</summary>
public sealed record ChangeStatusView(
    int Count,
    int Added,
    int Updated,
    int Removed,
    string BytesText);

/// <summary>
/// 变更清单的展示映射（对照 Node <c>public/js/app.js</c> 的
/// <c>rowHtml</c> / <c>visibleChanges</c> / <c>renderChanges</c> / <c>renderStatus</c>）。
/// </summary>
public static class ChangeList
{
    // ---- 标签样式键（与 Theme.xaml 里的 StaticResource 一一对应）----
    private const string TagNeutral = "PixelTagNeutralStyle";
    private const string TagAmber = "PixelTagAmberStyle";
    private const string TagRed = "PixelTagRedStyle";
    private const string TagBlue = "PixelTagBlueStyle";
    private const string TagPurple = "PixelTagPurpleStyle";

    /// <summary>
    /// 唯一键（Node <c>selKey = c =&gt; `${c.category}::${c.key}`</c>）。
    /// 分类要一起进键：不同分类下可能出现同名 key。
    /// </summary>
    public static string SelKey(ChangeItem c) => $"{c.Category}::{c.Key}";

    /// <summary>
    /// 把变更项转成行视图（Node <c>rowHtml</c>）。
    ///
    /// <para><b>标签顺序必须照抄</b>：你装的 → 降级 → 内容变 → 重复 → 已禁用 → 仅文件名不同。
    /// Node 是按这个顺序 push 的，顺序变了就是一种"排版变了"。</para>
    ///
    /// <para><b><c>duplicate</c> 与 <c>duplicated</c> 是互斥的</b>：Node 写的
    /// <c>if (c.duplicate) ... else if (c.duplicated)</c> —— 本地多余的重复项
    /// 要告诉玩家"保留哪一份"，而普通的"这侧有重复"只标红。
    /// 写成两个独立 if 会同时出现两个"重复"标签。</para>
    /// </summary>
    public static List<ChangeRowView> BuildRows(IEnumerable<ChangeItem> changes)
    {
        var rows = new List<ChangeRowView>();
        foreach (var c in changes)
        {
            var tags = new List<ChangeTagView>();

            if (c.PlayerInstalled == true)
            {
                tags.Add(new ChangeTagView("你装的", TagAmber));
            }
            if (c.Downgrade == true)
            {
                tags.Add(new ChangeTagView("降级", TagRed));
            }
            if (c.Reason == ChangeReason.Content && c.Type == ChangeType.Updated)
            {
                tags.Add(new ChangeTagView("版本号没变但内容变了", TagBlue));
            }
            if (c.Duplicate == true)
            {
                // 本地多余的重复 mod：明确说保留的是哪一份，默认不勾
                tags.Add(new ChangeTagView(
                    "重复·保留 " + Format.ShortPath(c.DuplicateOf, 22),
                    TagAmber,
                    $"和「{c.DuplicateOf}」重复，建议删掉这份保留另一份"));
            }
            else if (c.Duplicated == true)
            {
                tags.Add(new ChangeTagView("重复", TagRed));
            }
            if (c.Disabled == true)
            {
                tags.Add(new ChangeTagView("已禁用", TagNeutral));
            }
            if (c.Renamed == true)
            {
                tags.Add(new ChangeTagView("仅文件名不同", TagPurple));
            }

            rows.Add(new ChangeRowView(
                c,
                SelKey(c),
                c.DisplayName,
                c.FileName,
                // Node: `c.size ? bytes(c.size) : ''` —— 0 字节不显示
                c.Size != 0 ? Format.Bytes(c.Size) : "",
                c.Type is ChangeType.Updated or ChangeType.Removed ? c.OldVersion : null,
                c.Type is ChangeType.Updated or ChangeType.Added or ChangeType.Unchanged ? c.NewVersion : null,
                tags,
                CanCheck: c.Type != ChangeType.Unchanged));
        }
        return rows;
    }

    /// <summary>
    /// 按筛选与搜索关键词过滤（Node <c>visibleChanges</c>）。
    ///
    /// <para><b>「全部」视图会藏掉无变化项</b>：Node 写的是
    /// <c>if (S.filter === 'all' &amp;&amp; c.type === 'unchanged') return false</c>。
    /// 不藏的话，一个 90 个 mod 的整合包会列出 90 条"无变化"，
    /// 真正要处理的变更被淹掉 —— 但玩家想看时仍可点「无变化」筛出来。</para>
    /// </summary>
    public static List<ChangeRowView> Visible(IEnumerable<ChangeRowView> rows, string filter, string? query)
    {
        var q = (query ?? "").Trim().ToLowerInvariant();
        var result = new List<ChangeRowView>();

        foreach (var r in rows)
        {
            var type = r.Item.Type;
            if (filter != "all" && type != filter)
            {
                continue;
            }
            if (filter == "all" && type == ChangeType.Unchanged)
            {
                continue;
            }
            if (q.Length > 0
                && !r.DisplayName.ToLowerInvariant().Contains(q, StringComparison.Ordinal)
                && !r.FileName.ToLowerInvariant().Contains(q, StringComparison.Ordinal))
            {
                continue;
            }
            result.Add(r);
        }

        return result;
    }

    /// <summary>
    /// 按分类分组（Node <c>renderChanges</c> 的 <c>groups</c>）。
    ///
    /// <para><b>分组顺序取配置顺序</b>：Node 用 <c>Map</c> 按遍历顺序（即变更顺序）
    /// 分组，而变更已按 <c>category</c> 排序过，所以实际等价于字母序
    /// （config &lt; mods &lt; resourcepacks &lt; shaderpacks）。
    /// 这里直接用 <see cref="AppConfig.ManagedCategories"/> 的顺序 ——
    /// 与步骤 2「内含」标签、更新范围勾选的顺序一致，用户心智更连贯。</para>
    /// </summary>
    public static List<ChangeGroupView> Group(IEnumerable<ChangeRowView> rows)
    {
        var byCat = new Dictionary<string, List<ChangeRowView>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!byCat.TryGetValue(r.Item.Category, out var list))
            {
                byCat[r.Item.Category] = list = [];
            }
            list.Add(r);
        }

        var groups = new List<ChangeGroupView>();
        foreach (var (key, title, _, _) in AppConfig.ManagedCategories)
        {
            if (!byCat.TryGetValue(key, out var items))
            {
                continue;
            }
            groups.Add(new ChangeGroupView(
                key, title, items, items.Any(r => r.CanCheck)));
        }

        return groups;
    }

    /// <summary>
    /// 默认勾选集合（Node：<c>new Set(changes.filter(c =&gt; c.type !== 'unchanged' &amp;&amp; c.checked))</c>）。
    /// 「恢复推荐勾选」按钮就是把它重算一遍。
    /// </summary>
    public static HashSet<string> RecommendedKeys(IEnumerable<ChangeItem> changes)
        => changes.Where(c => c.Type != ChangeType.Unchanged && c.Checked)
                  .Select(SelKey)
                  .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// 底部统计（Node <c>renderStatus</c>）。
    ///
    /// <para><b>统计的是全部变更，不是当前可见的那部分</b>：Node 用
    /// <c>S.changes.filter(...)</c> 而非 <c>visibleChanges()</c>。
    /// 用可见集合会让"搜索一个词"这个动作改变"已勾选 N 项"，
    /// 玩家会以为自己刚取消了一堆勾选。</para>
    /// </summary>
    public static ChangeStatusView Status(IEnumerable<ChangeItem> changes, ISet<string> selected)
    {
        var added = 0; var updated = 0; var removed = 0;
        long size = 0;
        var count = 0;

        foreach (var c in changes)
        {
            if (!selected.Contains(SelKey(c)))
            {
                continue;
            }
            count++;
            switch (c.Type)
            {
                case ChangeType.Added: added++; break;
                case ChangeType.Updated: updated++; break;
                case ChangeType.Removed: removed++; break;
            }
            size += c.Size;
        }

        return new ChangeStatusView(count, added, updated, removed, Format.Bytes(size));
    }
}

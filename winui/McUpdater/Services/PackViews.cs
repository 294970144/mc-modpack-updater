namespace McUpdater.Services;

/// <summary>
/// 整合包信息卡「内含」行的标签项。
///
/// <para><b>为什么这些视图模型放在 Services 里而不在 MainWindow 里</b>：
/// 它们描述的是「读包结果 → 展示」的映射，属于读包链路的输出契约。
/// 放MainWindow 会让窗口类同时背上数据整形与 UI 构建两件事。</para>
/// </summary>
internal sealed class CategoryTagView
{
    /// <summary>分类中文名（Node 的 <c>label</c>）。</summary>
    public required string Title { get; init; }

    /// <summary>该分类在包内的文件数。</summary>
    public required int Count { get; init; }
}

/// <summary>更新范围的一个勾选项。</summary>
internal sealed class ScopeItemView
{
    /// <summary>分类键（mods / config / ...），与 <c>AppConfig.ManagedCategories</c> 一致。</summary>
    public required string Key { get; init; }

    /// <summary>分类中文名。</summary>
    public required string Title { get; init; }

    /// <summary>一句话说明这个分类被改动的影响（Node <c>renderScope</c> 的 desc 文案）。</summary>
    public required string Desc { get; init; }

    /// <summary>包里该分类的文件数。</summary>
    public required int Count { get; init; }

    /// <summary>包里没有这一类 → 勾选框禁用（CSS: opacity:.45 + not-allowed）。</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>初始是否勾选。</summary>
    public required bool DefaultOn { get; init; }
}
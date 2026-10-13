namespace McUpdater.Core;

/// <summary>
/// 变更类型（移植自 Node 版 <c>TYPE</c>）。
/// </summary>
/// <remarks>
/// 用 <c>static class</c> 持有字符串常量而不用 <c>enum</c>：Node 侧这些值会
/// 直接进 JSON（manifest、导出清单），并且 UI 是按字符串分桶渲染的。
/// 换成 enum 会多一层 <c>ToString()</c> 转换，且容易在某个 switch 里漏掉分支。
/// </remarks>
public static class ChangeType
{
    /// <summary>整合包有、本地没有。</summary>
    public const string Added = "added";

    /// <summary>两边都有但内容不同。</summary>
    public const string Updated = "updated";

    /// <summary>本地有、整合包没有。</summary>
    public const string Removed = "removed";

    /// <summary>字节级一致（不算变更，但可能只是文件名变了）。</summary>
    public const string Unchanged = "unchanged";

    /// <summary>重复安装（同一 modId/文件在同侧出现多次），加载器会拒绝启动。</summary>
    public const string Conflict = "conflict";
}

/// <summary>更新原因（<c>reason</c> 字段的取值）。</summary>
public static class ChangeReason
{
    /// <summary>版本号相同但内容不同，多半是服主重新打包 / 热修。</summary>
    public const string Content = "content";

    /// <summary>整合包里的版本比本地还旧。</summary>
    public const string Downgrade = "downgrade";

    /// <summary>正常版本升级。</summary>
    public const string Version = "version";
}

/// <summary>
/// 单条变更（对应 Node 版 <c>diff-*.js</c> 产出的 change 对象）。
///
/// <para><b>为什么是一个扁平大对象而不是按分类拆多个类型</b>：Node 版
/// <c>changes</c> 就是一个混装 mods / config / resourcepacks / shaderpacks
/// 四类 heterogeneous 对象的数组，UI 拿到后按 <c>category</c> 分桶、再按
/// <c>type</c> 排序渲染。C# 侧若拆成继承体系，反序列化与分组都要额外映射，
/// 且字段名保不住 Node 的原样，Node 基准回归就对不上了。
/// 这里保持扁平 + 可空，语义与 Node 严格一一对应。</para>
///
/// <para><b>字段一律用可空类型</b>：Node 里不同分支 <c>push</c> 的键集合是不一样
/// 的（比如 added 没有 <c>oldVersion</c>，removed 没有 <c>newVersion</c>，
/// 本地重复项才有 <c>duplicateOf</c>）。C# 用 <c>required</c> 强制全填会
/// 逼出一堆 <c>null!</c> 占位符，可空 + 文档说明才是忠实表达。</para>
/// </summary>
public sealed class ChangeItem
{
    // ---- 五类变更的公共字段 ----

    /// <summary>变更类型，见 <see cref="ChangeType"/>。</summary>
    public required string Type { get; init; }

    /// <summary>所属分类 key（mods / config / resourcepacks / shaderpacks）。</summary>
    public required string Category { get; init; }

    /// <summary>
    /// 唯一键。<b>顺序即输出顺序</b>：Node 用 <c>Set([...zipByKey.keys(),
    /// ...localByKey.keys()])</c> 遍历，JS Set 保插入序，所以变更清单的展示
    /// 顺序是「先 zip 侧新增、再本地侧新增」的稳定顺序。C# 必须照抄这个
    /// 插入序，不要"优化"成字典排序或类型分组排序 —— 那会让 UI 顺序与 Node
    /// 版对不上，也让对拍测试失去意义。</b>
    /// </summary>
    public required string Key { get; init; }

    /// <summary>UI 上显示的名字。优先用元数据里的 name，其次 id，最后 key。</summary>
    public required string DisplayName { get; init; }

    /// <summary>文件名（zip 侧取 relPath 的末段，本地侧就是文件名）。</summary>
    public required string FileName { get; init; }

    /// <summary>文件字节数。</summary>
    public required long Size { get; init; }

    /// <summary>默认勾选状态。这是 diff 引擎最重要的"人为判断"字段。</summary>
    public required bool Checked { get; init; }

    // ---- 仅 mods 分类 ----

    /// <summary>模组 ID（mods 分类）。解析失败时可能为空。</summary>
    public string? ModId { get; init; }

    /// <summary>加载器（mods 分类，取自整合包侧的元数据）。</summary>
    public string? Loader { get; init; }

    /// <summary>更新后的版本号（added / updated / unchanged 有值）。</summary>
    public string? NewVersion { get; init; }

    /// <summary>更新前的版本号（removed / updated / unchanged 有值）。</summary>
    public string? OldVersion { get; init; }

    /// <summary>本地旧的完整路径（removed / updated 有值）。</summary>
    public string? LocalPath { get; init; }

    /// <summary>zip 内的相对路径（added / updated 有值）。</summary>
    public string? ZipRelPath { get; init; }

    /// <summary>更新前的文件名（updated / unchanged-with-rename 有值）。</summary>
    public string? OldFileName { get; init; }

    /// <summary>
    /// 内容一致但文件名不同（PCL2 给 mod 加了中文前缀）。
    /// 这种情况 <b>不算变更</b>，但值得提示。
    /// </summary>
    public bool? Renamed { get; init; }

    /// <summary>
    /// 是否降级。Node: <c>cmp &lt; 0</c>。
    /// 与 <see cref="Reason"/> 同时出现，<see cref="Reason"/> 是它的字符串化。
    /// </summary>
    public bool? Downgrade { get; init; }

    /// <summary>变更原因，见 <see cref="ChangeReason"/>。</summary>
    public string? Reason { get; init; }

    /// <summary>本地该文件被玩家手动禁用（<c>.jar.disabled</c>）。</summary>
    public bool? Disabled { get; init; }

    // ---- 本地重复项专用 ----

    /// <summary>这是本地重复安装的冗余项。</summary>
    public bool? Duplicate { get; init; }

    /// <summary>被保留为代表的那一份的文件名。</summary>
    public string? DuplicateOf { get; init; }

    /// <summary>代表项的版本号。</summary>
    public string? DuplicateOfVersion { get; init; }

    // ---- 归属判断（三态，null ≠ false）-----

    /// <summary>
    /// 是否为玩家自己安装的（manifest 里找不到）。
    ///
    /// <para><b>三态语义必须保留</b>：<c>true</c> 玩家自装（默认不勾）、
    /// <c>false</c> 整合包装的（默认勾删除）、<c>null</c> 无 manifest 首次运行
    /// <em>无从判断</em>。把 null 压成 false 会让首次运行时把玩家自装的 mod
    /// 全部默认勾选删除 —— 这是会真实删掉玩家东西的 bug。</para>
    /// </summary>
    public bool? PlayerInstalled { get; init; }

    // ---- 两侧都有重复 ----

    /// <summary>
    /// 该 key 在本侧（zip 或 local）出现了多次。
    /// 注意这<em>不等于</em>产出移除项：zip 侧重复只走 conflicts 告警。
    /// </summary>
    public bool? Duplicated { get; init; }

    /// <inheritdoc/>
    public override string ToString()
        => $"{Type}/{Category} {Key} {FileName} ({(Checked ? "勾选" : "不勾选")})";
}

/// <summary>重复安装冲突（Node: <c>conflicts.push({side, key, displayName, files})</c>）。</summary>
public sealed class DiffConflict
{
    /// <summary>冲突发生在哪一侧：<c>zip</c> 或 <c>local</c>。</summary>
    public required string Side { get; init; }

    /// <summary>出问题的 key（<c>id:&lt;modId&gt;</c> 或 <c>file:&lt;文件名&gt;</c>）。</summary>
    public required string Key { get; init; }

    /// <summary>展示名。</summary>
    public required string DisplayName { get; init; }

    /// <summary>重复的具体文件列表。</summary>
    public required IReadOnlyList<DiffConflictFile> Files { get; init; }
}

/// <summary>冲突项里的单个文件。</summary>
/// <param name="FileName">文件名。</param>
/// <param name="Version">版本号。</param>
public readonly record struct DiffConflictFile(string FileName, string? Version);

/// <summary>
/// 被跳过的文件统计（Node: <c>skipped = {protected, oversize, ext, samples}</c>）。
/// </summary>
public sealed class DiffSkipped
{
    /// <summary>因保护规则或路径穿越被跳过的数量。</summary>
    public int Protected { get; init; }

    /// <summary>因超出单文件大小上限被跳过的数量。</summary>
    public int Oversize { get; init; }

    /// <summary>因扩展名不符（资源包只认 .zip）被跳过的数量。</summary>
    public int Ext { get; init; }

    /// <summary>被跳过文件的样本路径，最多 6 条（Node 里同样是 6）。</summary>
    public IReadOnlyList<string> Samples { get; init; } = [];
}

/// <summary>一次对比的产物。</summary>
public sealed class DiffResult
{
    /// <summary>变更清单（含 unchanged，顺序即展示顺序）。</summary>
    public required IReadOnlyList<ChangeItem> Changes { get; init; }

    /// <summary>重复安装冲突。</summary>
    public IReadOnlyList<DiffConflict> Conflicts { get; init; } = [];

    /// <summary>被跳过的统计（仅普通文件分类会填）。</summary>
    public DiffSkipped? Skipped { get; init; }
}
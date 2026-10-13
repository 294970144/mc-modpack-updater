using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McUpdater.Core;

namespace McUpdater.Services;

/// <summary>
/// M3 链路自检：把「读包结果 → UI 展示」这一步的映射逻辑从
/// <see cref="MainWindow"/> 里抽出来单独可测。
///
/// <para><b>为什么要抽</b>：<c>RenderPackInfo</c> / <c>RenderScope</c> 原本直接把
/// 值写进 <c>TextBlock.Text</c>。那样写的话，"包名取PackName 还是文件名"、
/// "空分类要不要显示" 这些规则就钉死在事件处理器里，既没法用测试覆盖，
/// 也没法在别处复用。抽成纯函数后，规则本身就是被断言的对象。</para>
///
/// <para><b>保真要点</b>：全部对齐 Node <c>public/js/app.js</c> 的
/// <c>renderPackInfo</c> 与 <c>renderScope</c>。</para>
/// </summary>
internal static class PackInfoMapper
{
    /// <summary>整合包信息卡的展示模型。</summary>
    internal sealed record PackInfo(
        string Name,
        string GameVersion,
        string? LoaderTag,
        IReadOnlyList<CategoryTagView> Categories,
        bool IsEmptyCategories,
        string FileLine);

    /// <summary>
    /// 包信息卡内容（Node <c>renderPackInfo</c>）。
    /// </summary>
    /// <param name="handle">已打开的整合包。</param>
    /// <param name="shortPath">路径缩短函数（Node <c>UI.shortPath</c>）。</param>
    /// <param name="formatBytes">体积格式化函数（Node <c>UI.bytes</c>）。</param>
    public static PackInfo Build(ModpackHandle handle, Func<string, int, string> shortPath,
        Func<long, string> formatBytes)
    {
        // Node: `p.packName || p.fileName` —— 空串是 falsy，会回落到文件名
        var name = string.IsNullOrEmpty(handle.PackName)
            ? Path.GetFileName(handle.ZipPath)
            : handle.PackName;

        // Node: `p.gameVersion || '未知'`
        var gameVersion = string.IsNullOrEmpty(handle.GameVersion) ? "未知" : handle.GameVersion;

        // Node: `p.loader ? tag-green(${loader} ${loaderVersion || ''}) : ''`
        string? loaderTag = null;
        if (!string.IsNullOrEmpty(handle.Loader))
        {
            loaderTag = string.IsNullOrEmpty(handle.LoaderVersion)
                ? handle.Loader
                : $"{handle.Loader} {handle.LoaderVersion}";
        }

        // Node: 只渲染 count>0 的分类，全空则显示「空」
        //【保真】分类顺序 = ManagedCategories 的顺序，与 Node 的 S.categories 一致
        var tags = new List<CategoryTagView>();
        foreach (var (key, title, _, _) in AppConfig.ManagedCategories)
        {
            var n = handle.CategoryCounts.TryGetValue(key, out var c) ? c : 0;
            if (n > 0)
            {
                tags.Add(new CategoryTagView { Title = title, Count = n });
            }
        }

        var size = new FileInfo(handle.ZipPath).Length;
        var fileLine = $"{shortPath(handle.ZipPath, 46)} · {formatBytes(size)}";

        return new PackInfo(name, gameVersion, loaderTag, tags, tags.Count == 0, fileLine);
    }

    /// <summary>范围项的说明文案（Node <c>renderScope</c> 的 desc 表）。</summary>
    internal static readonly IReadOnlyDictionary<string, string> ScopeDescriptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mods"] = "整合包的核心，一般都要更新",
            ["config"] = "模组的参数设置，你手动调过的会被覆盖",
            ["resourcepacks"] = "材质包文件",
            ["shaderpacks"] = "光影包文件",
        };

    /// <summary>
    /// 更新范围勾选项列表（Node <c>renderScope</c>）。
    ///
    /// <para><b>默认勾选的两条件都要</b>：<c>defaultOn</c> 且包里确有内容。
    /// 只看 <c>defaultOn</c> 会把空分类勾上；只看有无内容则 config 会被默认打开
    /// （它 defaultOn=false，但一旦有内容就参与更新 —— 而 config 覆盖的是
    /// 玩家手调过的参数，是四类里唯一有破坏性的）。</para>
    /// </summary>
    public static List<ScopeItemView> BuildScope(ModpackHandle handle)
    {
        var result = new List<ScopeItemView>();
        foreach (var (key, title, defaultOn, _) in AppConfig.ManagedCategories)
        {
            var n = handle.CategoryCounts.TryGetValue(key, out var c) ? c : 0;
            result.Add(new ScopeItemView
            {
                Key = key,
                Title = title,
                Desc = ScopeDescriptions.TryGetValue(key, out var d) ? d : "",
                Count = n,
                // 【保真】空分类即使 defaultOn 也不勾
                DefaultOn = defaultOn && n > 0,
            });
        }
        return result;
    }
}
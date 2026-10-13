using System.Globalization;

namespace McUpdater.Core;

/// <summary>
/// 一次对比的汇总（Node <c>src/router.js</c> 里 <c>runDiff</c> 的 <c>summary</c>）。
/// </summary>
/// <param name="Added">整合包有、本地没有。</param>
/// <param name="Updated">两边都有但内容不同。</param>
/// <param name="Removed">本地有、整合包没有。</param>
/// <param name="Unchanged">字节级一致。</param>
/// <param name="Changed">前三项之和（= 真正会动的条目数）。</param>
/// <param name="Bytes">勾选中的非无变化项的字节数合计。</param>
/// <param name="BytesText"><see cref="Bytes"/> 的展示文案。</param>
/// <param name="PlayerInstalledRemovals">玩家自装、且不在整合包里的移除项数量。</param>
/// <param name="FirstRun">目标目录里没有安装记录 → 分不清哪些是玩家自装的。</param>
/// <param name="Skipped">被跳过的文件统计。</param>
public sealed record DiffSummary(
    int Added,
    int Updated,
    int Removed,
    int Unchanged,
    int Changed,
    long Bytes,
    string BytesText,
    int PlayerInstalledRemovals,
    bool FirstRun,
    DiffSkipped Skipped);

/// <summary>一次对比的完整产物（Node 的 <c>state.diff</c>）。</summary>
public sealed record DiffPlan(
    IReadOnlyList<ChangeItem> Changes,
    IReadOnlyList<DiffConflict> Conflicts,
    DiffSummary Summary,
    IReadOnlyList<string> Categories);

/// <summary>
/// 对比编排（移植自 Node <c>src/router.js</c> 的 <c>runDiff</c>）。
///
/// <para><b>为什么不放在 Services 里由 MainWindow 直接调 DiffEngine</b>：
/// 这一层不只有循环 —— 它带着四条会直接影响用户判断的规则：
/// 分类按配置顺序而非用户勾选顺序、变更按"需要注意的排前面"排序、
/// 只统计<em>勾选中的</em>写入量、以及把 zip/local 两侧的同源冲突合并成一条。
/// 这些写进事件处理器就等于放弃测试覆盖。</para>
///
/// <para><b>不写缓存文件是刻意的</b>：Node 在跑完 diff 后调
/// <c>cache.save()</c> 把 CRC 落到 <c>.mcupdater-cache.json</c>。
/// 本次范围不含 apply，缓存落盘没有后续收益，
/// 却会在玩家的游戏目录里留下一个文件 —— **真实样本是只读约定，不写。**
/// 缓存仍在内存里生效（同一次对比内避免重复读 jar）。</para>
/// </summary>
public static class DiffPlanner
{
    /// <summary>
    /// 变更类型的展示优先级（Node <c>TYPE_ORDER</c>）。
    /// 数值越小越靠前 —— 需要玩家注意的排前面。
    /// </summary>
    private static int TypeOrder(string type) => type switch
    {
        ChangeType.Updated => 0,
        ChangeType.Added => 1,
        ChangeType.Removed => 2,
        ChangeType.Unchanged => 3,
        _ => 9,
    };

    /// <summary>
    /// 跑一次对比。
    /// </summary>
    /// <param name="pack">已打开的整合包。</param>
    /// <param name="contentDir">目标版本的内容目录（版本隔离时是版本目录，否则 .minecraft 根）。</param>
    /// <param name="categories">用户勾选的分类（顺序无意义，内部按配置顺序跑）。</param>
    /// <param name="onProgress">进度回调（文案, 百分比）。</param>
    public static DiffPlan Run(ModpackHandle pack, string contentDir,
        IReadOnlyCollection<string> categories, Action<string, int>? onProgress = null)
    {
        // 【保真】分类顺序取配置顺序，不是用户勾选顺序。
        // Node: `config.MANAGED_CATEGORIES.filter(c => categories.includes(c.key))`
        // —— 用户的 scope 是个 Set，顺序本来就不定，靠配置顺序兜住输出稳定。
        var active = AppConfig.ManagedCategories
            .Where(c => categories.Contains(c.Key))
            .ToList();

        if (active.Count == 0)
        {
            throw new InvalidOperationException("至少要选一个更新范围");
        }

        var manifest = Manifest.Load(contentDir);
        var cache = new MetaCache();     // 内存态，不落盘（见类注释）
        var all = new List<ChangeItem>();
        var conflicts = new List<DiffConflict>();
        var skipped = new SkipCounter();

        var done = 0;
        foreach (var cat in active)
        {
            var zipItems = pack.ListCategory(cat.Key);
            var localDir = Path.Combine(contentDir, cat.Key);
            onProgress?.Invoke($"正在对比{cat.Title}…", 40 + (int)Math.Round(done * 45.0 / active.Count));

            if (cat.ByModId)
            {
                var r = DiffEngine.DiffMods(zipItems, localDir, pack, manifest, cache, onProgress);
                all.AddRange(r.Changes);
                conflicts.AddRange(r.Conflicts);
            }
            else
            {
                var r = DiffEngine.DiffFiles(cat.Key, zipItems, localDir, manifest);
                all.AddRange(r.Changes);
                if (r.Skipped is not null)
                {
                    // 【保真，实测被基准抓到】分类前缀是 router 在<em>聚合</em>时加的：
                    // Node 写的是 `skipped.samples.push(`${cat.dir}/${s}`)`。
                    // DiffFiles 自己返回的是分类内的相对路径（.puzzle_cache/...），
                    // 少了这一层，用户看到的"例如："就不知道是哪个分类下的。
                    skipped.Add(r.Skipped.Protected, r.Skipped.Oversize, r.Skipped.Ext,
                        r.Skipped.Samples.Select(s => cat.Key + "/" + s).ToList());
                }
            }
            done++;
        }

        var zh = StringComparer.Create(new CultureInfo("zh-CN"), ignoreCase: false);
        var sorted = all
            .OrderBy(c => TypeOrder(c.Type))
            .ThenBy(c => c.Category, StringComparer.Ordinal)
            .ThenBy(c => c.DisplayName, zh)
            .ToList();

        var summary = BuildSummary(sorted, manifest, skipped.Build());

        return new DiffPlan(
            sorted,
            MergeConflicts(conflicts),
            summary,
            active.Select(c => c.Key).ToList());
    }

    /// <summary>
    /// 汇总统计（Node <c>runDiff</c> 里的 <c>summary</c> 计算）。
    ///
    /// <para><b><see cref="DiffSummary.Bytes"/> 只统计"勾选中的"非无变化项</b>：
    /// Node 写的是 <c>if (c.type !== UNCHANGED &amp;&amp; c.checked)</c>。
    /// 统计全部会让"写入约 XX MB"这个数字虚高 —— 玩家看到的体积必须
    /// 跟他实际勾的东西一致，否则这个数字就失去了决策价值。</para>
    ///
    /// <para><b><c>FirstRun</c> 的三段判据要照抄</b>：
    /// <c>!manifest || !Array.isArray(manifest.mods) || manifest.mods.length === 0</c>。
    /// C# 侧 <see cref="Manifest.Mods"/> 恒为非空列表，所以只剩"manifest 为 null"
    /// 与"列表为空"两条；但语义上必须仍然是"没有安装记录"，
    /// 因为它决定了移除项默认全不勾（防止误删玩家自装的 mod）。</para>
    /// </summary>
    private static DiffSummary BuildSummary(IReadOnlyList<ChangeItem> changes, Manifest? manifest,
        DiffSkipped skipped)
    {
        var added = 0; var updated = 0; var removed = 0; var unchanged = 0;
        long bytes = 0;
        var playerInstalledRemovals = 0;

        foreach (var c in changes)
        {
            switch (c.Type)
            {
                case ChangeType.Added: added++; break;
                case ChangeType.Updated: updated++; break;
                case ChangeType.Removed: removed++; break;
                case ChangeType.Unchanged: unchanged++; break;
            }

            if (c.Type != ChangeType.Unchanged && c.Checked)
            {
                bytes += c.Size;
            }

            // 【三态】只有明确为 true 才算"玩家自装"；null（无 manifest）不算。
            // 压成 true 会让首次运行时把所有移除项都标成"你装的"，误导玩家。
            if (c.Type == ChangeType.Removed && c.PlayerInstalled == true)
            {
                playerInstalledRemovals++;
            }
        }

        return new DiffSummary(
            added, updated, removed, unchanged,
            added + updated + removed,
            bytes, Format.Bytes(bytes),
            playerInstalledRemovals,
            manifest is null || manifest.Mods.Count == 0,
            skipped);
    }

    /// <summary>
    /// 合并 zip / local 两侧的同源冲突（Node <c>mergeConflicts</c>）。
    ///
    /// <para><b>为什么要合并</b>：玩家的 mods 目录本来就是上次从整合包装的，
    /// 所以整合包里重复的 mod，本地必然也重复 —— 同一个问题会在两侧各报一次，
    /// 玩家看到"两处重复"会以为要删两个地方。文件列表相同时合成一条，
    /// 并把 <see cref="DiffConflict.Side"/> 标成 <c>both</c>（说清根因在整合包：
    /// 玩家删了本地那份，下次更新还会被装回来）。</para>
    ///
    /// <para><b>文件列表不同时不合并</b>：那说明两侧各是各的重复，是两个独立问题。</para>
    /// </summary>
    public static List<DiffConflict> MergeConflicts(IReadOnlyList<DiffConflict> list)
    {
        var groups = new Dictionary<string, List<DiffConflict>>(StringComparer.Ordinal);
        foreach (var c in list)
        {
            if (!groups.TryGetValue(c.Key, out var g))
            {
                groups[c.Key] = g = [];
            }
            g.Add(c);
        }

        var out_ = new List<DiffConflict>();
        foreach (var (key, group) in groups)
        {
            if (group.Count == 1)
            {
                out_.Add(group[0]);
                continue;
            }

            var sig = string.Join("|", group[0].Files.Select(f => f.FileName).OrderBy(x => x, StringComparer.Ordinal));
            var same = group.All(c =>
                string.Join("|", c.Files.Select(f => f.FileName).OrderBy(x => x, StringComparer.Ordinal)) == sig);

            if (same)
            {
                var hasZip = group.Any(c => c.Side == "zip");
                out_.Add(new DiffConflict
                {
                    Key = key,
                    Side = hasZip ? "both" : group[0].Side,
                    DisplayName = group[0].DisplayName,
                    Files = group[0].Files,
                });
            }
            else
            {
                out_.AddRange(group);
            }
        }

        return out_;
    }

    /// <summary>跨分类累加被跳过的统计（Node 的 <c>skipped</c> 累加）。</summary>
    private sealed class SkipCounter
    {
        private int _protected;
        private int _oversize;
        private int _ext;
        private readonly List<string> _samples = [];

        public void Add(int p, int o, int e, IReadOnlyList<string> samples)
        {
            _protected += p;
            _oversize += o;
            _ext += e;
            // Node: 样本最多 8 条（router.js: `if (skipped.samples.length < 8)`）
            foreach (var s in samples)
            {
                if (_samples.Count < 8)
                {
                    _samples.Add(s);
                }
            }
        }

        public DiffSkipped Build() => new()
        {
            Protected = _protected,
            Oversize = _oversize,
            Ext = _ext,
            Samples = _samples,
        };
    }
}

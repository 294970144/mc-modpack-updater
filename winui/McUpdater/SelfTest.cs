using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using McUpdater.Core;
using McUpdater.Services;

namespace McUpdater;

/// <summary>
/// 命令行自检入口：<c>McUpdater.exe --selftest "&lt;整合包路径&gt;"</c>。
///
/// <para><b>为什么需要它</b>：WinUI 3 / DirectComposition 的窗口内容
/// <b>无法用PrintWindow 抓到</b>（标题栏能抓、客户区全白），屏幕抓取在
/// 无头会话里又抓不到窗口（本机实测：窗口移到 (0,0) 后只拍到桌面）。
/// 于是「UI 上显示的数字对不对」这个M3 的核心验收项没法靠截图证明。
///
/// <para>但真正要验的不是像素，是<b>数据映射</b>：包名取哪个字段、
/// 分类计数怎么算、loader 标签怎么拼。这些逻辑都在
/// <see cref="PackInfoMapper"/> 里，程序内跑一遍并把结果写进日志，
/// 比截图更精确 —— 截图只能证明"看起来有字"，自检能证明"字是什么"。</para>
///
/// <para>日志写到 <c>&lt;LocalAppData&gt;/McUpdater/logs/selftest.log</c>，
/// stdout 同时输出，方便命令行直接看。</para>
/// </summary>
internal static class SelfTest
{
    /// <summary>
    /// 本次自检跑出的对比结果。供 <c>--show --step3</c> 截图模式取用 ——
    /// 变更清单面板要有真实数据才有截图价值，而这份数据在自检里已经算过一次了，
    /// 没必要为了截图再跑一遍（那要 2.5 秒）。
    /// </summary>
    internal static DiffPlan? LastPlan { get; private set; }

    /// <param name="packPath">待自检的整合包路径。</param>
    /// <param name="window">
    /// 真实窗口实例。自检里要构造范围勾选项这类真实控件，
    /// 所以必须有一个已InitializeComponent 的窗口来提供资源字典与 DispatcherQueue。
    /// </param>
    public static async Task<int> RunAsync(string packPath, MainWindow? window = null)
    {
        var log = new List<string>();

        void Say(string line)
        {
            log.Add(line);
            Console.WriteLine(line);
        }

        try
        {
            Say($"=== M4 自检开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            Say($"包路径：{packPath}");

            if (!File.Exists(packPath))
            {
                Say($"结果：文件不存在");
                return 2;
            }

            // ---- 读包（走UI 侧同一条链路：PackLoadService → ModpackReader.Open）----
            var sw = Stopwatch.StartNew();
            var frames = 0;
            var lastPct = -1;
            var monotonic = true;

            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            using var handle = await PackLoadService.OpenAsync(
                packPath, dispatcher, (msg, pct) =>
                {
                    frames++;
                    if (pct < lastPct) monotonic = false;
                    lastPct = pct;
                });
            sw.Stop();

            Say($"读包耗时：{sw.ElapsedMilliseconds} ms，进度帧数：{frames}");
            Say($"进度单调不减：{(monotonic ? "是" : "否")}");

            // ---- 原始句柄字段（步骤 2 卡片的数据来源）----
            Say("");
            Say("---- 句柄字段 ----");
            Say($"格式：{handle.Format}");
            Say($"内容根：{handle.ContentRoot}");
            Say($"包名：{handle.PackName}");
            Say($"包版本：{handle.PackVersion}");
            Say($"游戏版本：{handle.GameVersion}");
            Say($"加载器：{handle.Loader} {handle.LoaderVersion}");
            Say($"版本名提示：{handle.VersionNameHint}");
            Say($"隔离：{handle.IsolationHint}");
            Say($"大小：{Format.Bytes(new FileInfo(handle.ZipPath).Length)}");

            Say("");
            Say("---- 分类计数（原始）----");
            foreach (var (key, title, _, _) in AppConfig.ManagedCategories)
            {
                handle.CategoryCounts.TryGetValue(key, out var n);
                Say($"{key,-16} {title,-8} {n}");
            }

            // ---- PackInfoMapper 的输出：这就是 UI 上会显示的 ----
            Say("");
            Say("---- 步骤 2 整合包卡（UI 显示值）----");
            var info = PackInfoMapper.Build(handle, Format.ShortPath, Format.Bytes);
            Say($"名称：{info.Name}");
            Say($"游戏版本：{info.GameVersion}");
            Say($"加载器标签：{(info.LoaderTag ?? "（无）")}");
            Say($"内含：{(info.IsEmptyCategories ? "空" : string.Join("  ", info.Categories.Select(t => $"{t.Title} {t.Count}")))}");
            Say($"文件：{info.FileLine}");

            Say("");
            Say("---- 步骤 2 更新范围（UI 显示值）----");
            var scope = PackInfoMapper.BuildScope(handle);
            foreach (var s in scope)
            {
                Say($"{s.Title,-8} 数量={s.Count,-5} 默认勾选={(s.DefaultOn ? "是" : "否"),-4} 禁用={(s.IsEmpty ? "是" : "否")}  说明={s.Desc}");
            }

            // ---- M4：PCL2 探测 → 版本扫描 → 目标匹配 ----
            Say("");
            Say("---- 步骤 2 目标（M4）----");
            // 三段分别计时：一次 6 秒的卡顿到底卡在进程探测还是扫盘，
            // 只看总数永远定位不到（这次就是靠它定位到进程探测的）。
            var tProc = Stopwatch.StartNew();
            var procState = Pcl2Detector.CheckRunning();
            tProc.Stop();
            var tScan = Stopwatch.StartNew();
            var detected = Pcl2Detector.Detect(probe: () => procState);
            tScan.Stop();
            var tVer = Stopwatch.StartNew();
            var versions = ClientScanner.ScanAll(detected.Folders);
            var (best, ranked) = ClientScanner.MatchTarget(
                versions, handle.VersionNameHint, handle.GameVersion, handle.Loader);
            tVer.Stop();
            var scanSw = tProc.ElapsedMilliseconds + tScan.ElapsedMilliseconds + tVer.ElapsedMilliseconds;

            Say($"分段耗时：进程探测 {tProc.ElapsedMilliseconds} ms，" +
                $"启动器探测 {tScan.ElapsedMilliseconds} ms，版本扫描 {tVer.ElapsedMilliseconds} ms");
            Say($"进程探测：running={procState.Running}，exe={procState.ExePath ?? "（未取得）"}");

            Say($"PCL2：{(detected.Found ? "已找到" : "未找到")}，启动器目录 {detected.Roots.Count} 个，" +
                $"游戏文件夹 {detected.Folders.Count} 个，扫描用时 {scanSw} ms");
            foreach (var f in detected.Folders)
            {
                Say($"  文件夹 {f.Name,-16} {f.Path}{(f.IsSelected ? "  [选中]" : "")}  （来源 {f.Source}）");
            }

            Say($"游戏版本 {versions.Count} 个：");
            foreach (var v in versions)
            {
                Say($"  {v.Name,-24} {v.FolderName,-10} mod={v.ModCount,-4} " +
                    $"mc={v.GameVersion ?? "-",-9} loader={v.Loader ?? "-",-9} " +
                    $"隔离={(v.Isolated ? "是" : "否")}({v.IsolationBasis}) " +
                    $"管理中={(v.Managed ? "是" : "否")} 选中={(v.IsSelected ? "是" : "否")}");
                Say($"     内容目录 {v.ContentDir}");
            }

            Say($"自动匹配：{(best is null ? "（没匹配上，需要手动选择）" : best.Name)}");
            if (best is not null)
            {
                Say($"  目录：{best.ContentDir}");
                Say($"  得分：{best.Score}（{string.Join("、", best.Why)}）");
            }

            Say("");
            Say("---- 步骤 2 目标卡（UI 显示值）----");
            var warnings = TargetMapper.BuildWarnings(handle, best);
            var card = TargetMapper.Build(best, warnings, Format.ShortPath);
            if (!card.HasTarget)
            {
                Say("（未匹配：卡片显示「没能自动认出该更新哪个版本」）");
            }
            else
            {
                Say($"版本：{card.Name} （{card.FolderName}）");
                Say($"标签：{(card.Tags.Count == 0 ? "（无）" : string.Join("  ", card.Tags.Select(t => t.Text)))}");
                Say($"现有模组：{card.ModCount} 个");
                Say($"目录：{card.DirLine}");
                Say($"警告：{(card.Warnings.Count == 0 ? "（无）" : string.Join(" | ", card.Warnings))}");
                Say($"匹配依据：{(card.WhyLine.Length == 0 ? "（无）" : card.WhyLine)}");
            }

            // ---- M5：对比 + 变更清单 ----
            Say("");
            Say("---- 步骤 3 变更清单（M5）----");
            DiffPlan? plan = null;
            List<ChangeRowView> rows = [];
            var recommended = new HashSet<string>(StringComparer.Ordinal);

            if (best is not null)
            {
                // 默认范围与 UI 一致：defaultOn 且包里确有内容 → 只有模组
                var diffScope = PackInfoMapper.BuildScope(handle)
                    .Where(s => s.DefaultOn).Select(s => s.Key).ToList();

                var diffSw = Stopwatch.StartNew();
                plan = DiffPlanner.Run(handle, best.ContentDir, diffScope, (_, _) => { });
                LastPlan = plan;
                diffSw.Stop();

                rows = ChangeList.BuildRows(plan.Changes);
                recommended = ChangeList.RecommendedKeys(plan.Changes);

                Say($"范围：{string.Join("、", diffScope)}，对比耗时 {diffSw.ElapsedMilliseconds} ms");
                var s = plan.Summary;
                Say($"统计：新增 {s.Added} / 更新 {s.Updated} / 移除 {s.Removed} / 无变化 {s.Unchanged}" +
                    $" ｜ 变更 {s.Changed} 项，写入约 {s.BytesText}");
                Say($"玩家自装移除项 {s.PlayerInstalledRemovals} 个，首次运行={(s.FirstRun ? "是" : "否")}，" +
                    $"跳过 {s.Skipped.Protected + s.Skipped.Oversize} 个文件");
                Say($"重复冲突 {plan.Conflicts.Count} 处");

                Say("前 15 行（UI 显示值）：");
                foreach (var r in rows.Take(15))
                {
                    Say($"  [{r.Item.Type,-9}] {r.DisplayName,-40} {r.SizeText,8}" +
                        $"  {(r.Tags.Count == 0 ? "" : string.Join(" ", r.Tags.Select(t => t.Text)))}");
                }
            }
            else
            {
                Say("（没匹配到目标版本，跳过对比）");
            }

            // ---- 关键不变量 ----
            Say("");
            Say("---- 不变量检查 ----");
            var problems = 0;

            // 0) 范围勾选项的控件构造
            //
            // 【为什么数据映射对了还不够】本项目踩过的最贵的一个 bug 是：
            // PackInfoMapper 的输出全对，但 MainWindow.MakeScopeToggle 里
            // 有一行 `((Grid)box.Children[0])` 强转（实际是 Border），
            // 抛 InvalidCastException。读包成功 → 渲染步骤 2 时炸 →
            // 遮罩没关 → 进度条永远停在 35%、整个窗口点不动。
            // 数据层测试对这类 bug 完全无感，因为它压根不碰控件。
            //
            // 所以这里必须真的构造一遍控件。SelfTest 跑在 DispatcherQueue
            // 上（App.OnLaunched 里），构造 ToggleButton 是安全的。
            if (window is not null)
            {
                Say("");
                Say("---- 范围勾选项控件构造 ----");
                foreach (var s in scope)
                {
                    try
                    {
                        var toggle = window.MakeScopeToggle(s);
                        toggle.IsChecked = s.DefaultOn;   // 触发 Checked/Unchecked → 勾号显隐
                        toggle.IsEnabled = !s.IsEmpty;

                        // 必须真的走到模板应用，否则 VisualState 的坏 Target
                        // 不会在构造阶段暴露（要等控件进视觉树）
                        var ok = toggle.ApplyTemplate();
                        Say($"[ok] {s.Title,-8} 构造成功，ApplyTemplate={ok}");
                    }
                    catch (Exception ex)
                    {
                        Say($"[FAIL] {s.Title} 控件构造抛异常：{ex.GetType().Name}: {ex.Message}");
                        problems++;
                    }
                }
            }
            else
            {
                Say("[WARN] 未提供窗口实例，跳过控件构造检查");
            }

            // 1) 名称不能为空（否则卡片第一行是空白）
            if (string.IsNullOrWhiteSpace(info.Name)) { Say("[FAIL] 名称为空"); problems++; }
            else Say("[ok] 名称非空");

            // 2) 至少要有一个分类有内容（否则界面只剩一行"空"，用户无从判断包是否有效）
            if (info.IsEmptyCategories) { Say("[WARN] 所有分类计数为 0"); problems++; }
            else Say("[ok] 至少一个分类有内容");

            // 3) 范围项数量必须等于配置里的分类数
            if (scope.Count != AppConfig.ManagedCategories.Length)
            {
                Say($"[FAIL] 范围项数{scope.Count} != 分类数 {AppConfig.ManagedCategories.Length}");
                problems++;
            }
            else Say("[ok] 范围项数与分类数一致");

            // 4) 空分类绝不能被勾上
            foreach (var s in scope.Where(s => s.IsEmpty && s.DefaultOn))
            {
                Say($"[FAIL] 空分类 {s.Key} 被默认勾选");
                problems++;
            }
            if (!scope.Any(s => s.IsEmpty && s.DefaultOn)) Say("[ok] 空分类均未勾选");

            // 5) config 默认不勾（覆盖玩家调参，是四类里唯一有破坏性的）
            var cfg = scope.FirstOrDefault(s => s.Key == "config");
            if (cfg is not null && cfg.DefaultOn)
            {
                Say("[WARN] config 被默认勾选（Node 原版 defaultOn=false，此处偏离）");
                problems++;
            }
            else Say("[ok] config 默认未勾选");

            // ---- M4 不变量 ----
            Say("");
            Say("---- 不变量检查（M4 目标）----");

            // 6) 目标卡的标签控件构造
            //
            // 与范围勾选项同一个道理：数据对了不代表控件构造得出来。
            // 样式键写错（比如少写 Style 后缀）只在 ApplyTemplate/渲染时才炸，
            // 而那一步在窗口里才发生 —— 自检必须提前把它逼出来。
            if (window is not null && card.HasTarget)
            {
                foreach (var tag in card.Tags)
                {
                    try
                    {
                        // MakeTag 是 static —— 静态成员不能用实例访问（CS0176）
                        MainWindow.MakeTag(tag.Text, tag.StyleKey);
                        Say($"[ok] 标签「{tag.Text}」构造成功");
                    }
                    catch (Exception ex)
                    {
                        Say($"[FAIL] 标签「{tag.Text}」构造抛异常：{ex.GetType().Name}: {ex.Message}");
                        problems++;
                    }
                }
            }
            else if (card.HasTarget)
            {
                Say("[WARN] 未提供窗口实例，跳过标签构造检查");
            }

            // 6') 真渲染一次目标卡。
            // 这一步验的不是数据，是「x:Name 有没有写对」——
            // TargetMapper 全对但控件名拼错时，崩在选包之后、被遮罩盖住，
            // 表现就是"进度条卡住、窗口点不动"（M3 那个 bug 的同一类）。
            if (window is not null)
            {
                try
                {
                    window.RenderStep2ForSelfTest(handle, best);
                    Say("[ok] 步骤 2 面板渲染成功（控件名全部命中）");
                }
                catch (Exception ex)
                {
                    Say($"[FAIL] 目标卡渲染抛异常：{ex.GetType().Name}: {ex.Message}");
                    problems++;
                }
            }
            else
            {
                Say("[WARN] 未提供窗口实例，跳过目标卡渲染检查");
            }

            // 6') 「手动指定」态的目标卡。
            // 自动匹配态截不到这个标签（它只在用户手动选目录时出现），
            // 而截图要等会话解锁 —— 所以先在数据层把标签规则钉死。
            var manualCard = TargetMapper.Build(new ClientVersion
            {
                Name = "手动指定的版本",
                VersionDir = "x",
                ContentDir = "x",
                Isolated = true,
                IsolationBasis = "manual",
                Manual = true,
                ModCount = 3,
            }, [], Format.ShortPath);
            if (manualCard.Tags.Any(t => t.Text == "手动指定")
                && manualCard.Tags.All(t => t.Text != "PCL2 当前选中")
                && manualCard.WhyLine.Length == 0)
            {
                Say($"[ok] 手动指定态标签：{string.Join(" / ", manualCard.Tags.Select(t => t.Text))}（无匹配依据行）");
            }
            else
            {
                Say($"[FAIL] 手动指定态标签异常：{string.Join(" / ", manualCard.Tags.Select(t => t.Text))}");
                problems++;
            }

            // 7) 必须扫到东西 —— 本机装了 PCL2，扫不到就是探测逻辑回归了
            if (detected.Folders.Count == 0) { Say("[FAIL] 一个游戏文件夹都没扫到"); problems++; }
            else Say($"[ok] 扫描到 {detected.Folders.Count} 个游戏文件夹");

            if (versions.Count == 0) { Say("[FAIL] 一个游戏版本都没扫到"); problems++; }
            else Say($"[ok] 扫描到 {versions.Count} 个游戏版本");

            // 8) 排序必须按分数降序（升序会让用户看到"最不匹配的排第一"）
            var descending = true;
            for (var i = 1; i < ranked.Count; i++)
            {
                if (ranked[i].Score > ranked[i - 1].Score) descending = false;
            }
            if (!descending) { Say("[FAIL] 候选列表不是按匹配度降序"); problems++; }
            else Say("[ok] 候选列表按匹配度降序");

            // 9) 内容目录必须真实存在 —— 隔离判定错了会指向一个不存在的目录，
            //    而这种错误在"选包 → 对比"之前完全不可见。
            var badDir = versions.FirstOrDefault(v => !Directory.Exists(v.ContentDir));
            if (badDir is not null)
            {
                Say($"[FAIL] 版本 {badDir.Name} 的内容目录不存在：{badDir.ContentDir}");
                problems++;
            }
            else Say("[ok] 所有版本的内容目录都存在");

            // 10) 匹配上了就必须可用。没匹配上不是 bug（合法状态），只提示。
            if (best is null)
            {
                Say("[WARN] 没能自动匹配到目标版本（合法状态，界面会提示手动选择）");
            }
            else if (!Directory.Exists(best.ContentDir))
            {
                Say($"[FAIL] 目标内容目录不存在：{best.ContentDir}");
                problems++;
            }
            else Say($"[ok] 目标内容目录存在：{best.ContentDir}");

            // ---- M5 不变量 ----
            Say("");
            Say("---- 不变量检查（M5 变更清单）----");

            if (plan is null)
            {
                Say("[WARN] 没匹配到目标，跳过变更清单检查");
            }
            else
            {
                // 11) 步骤 3 真渲染（验 x:Name 与行构造）
                if (window is not null)
                {
                    try
                    {
                        window.RenderStep3ForSelfTest(plan);
                        Say("[ok] 步骤 3 面板渲染成功（控件名全部命中）");
                    }
                    catch (Exception ex)
                    {
                        Say($"[FAIL] 步骤 3 渲染抛异常：{ex.GetType().Name}: {ex.Message}");
                        problems++;
                    }
                }
                else
                {
                    Say("[WARN] 未提供窗口实例，跳过步骤 3 渲染检查");
                }

                // 12) 推荐勾选必须落在"非无变化"里 —— 把 unchanged 勾上会让
                //     应用阶段去写一个内容完全一样的文件
                var badChecked = plan.Changes
                    .FirstOrDefault(c => c.Type == ChangeType.Unchanged && recommended.Contains(ChangeList.SelKey(c)));
                if (badChecked is not null) { Say($"[FAIL] 无变化项被默认勾选：{badChecked.DisplayName}"); problems++; }
                else Say("[ok] 无变化项均未被默认勾选");

                // 13) 状态栏统计必须与实际勾选一致（数字对不上就会误导"写入多少"）
                var st = ChangeList.Status(plan.Changes, recommended);
                if (st.Count != recommended.Count)
                {
                    Say($"[FAIL] 状态栏计数 {st.Count} != 勾选数 {recommended.Count}");
                    problems++;
                }
                else Say($"[ok] 状态栏计数与勾选一致（{st.Count} 项，写入约 {st.BytesText}）");

                // 14) 分组不能漏项：各组行数之和 == 可见行数
                var visible = ChangeList.Visible(rows, "all", null);
                var grouped = ChangeList.Group(visible).Sum(g => g.Rows.Count);
                if (grouped != visible.Count)
                {
                    Say($"[FAIL] 分组后 {grouped} 行 != 可见 {visible.Count} 行");
                    problems++;
                }
                else Say($"[ok] 分组未漏项（{grouped} 行）");

                // 15) 「全部」视图必须藏掉无变化项
                if (visible.Any(r => r.Item.Type == ChangeType.Unchanged))
                {
                    Say("[FAIL] 「全部」视图里出现了无变化项");
                    problems++;
                }
                else Say("[ok] 「全部」视图已隐藏无变化项");
            }

            Say("");
            Say($"=== 自检结束：问题 {problems} 项 ===");

            Flush(log);
            return problems == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Say($"[异常] {ex.GetType().Name}: {ex.Message}");
            Say(ex.StackTrace ?? "");
            Flush(log);
            return 3;
        }
    }

    /// <summary>日志落盘。写失败不影响流程（自检结果已在 stdout）。</summary>
    private static void Flush(List<string> lines)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "McUpdater", "logs");
            Directory.CreateDirectory(dir);
            File.WriteAllLines(
                Path.Combine(dir, "selftest.log"),
                lines,
                System.Text.Encoding.UTF8);
        }
        catch
        {
            // 忽略
        }
    }
}
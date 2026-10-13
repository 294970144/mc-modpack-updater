using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using McUpdater.Core;
using McUpdater.Services;

namespace McUpdater;

/// <summary>
/// 主窗口。流程四步：选择整合包 → 确认目标版本 → 检查变更 → 完成。
///
/// <para>本次交付范围到 M5「变更清单」为止 —— 不含应用更新、回滚、备份管理、
/// 导出清单、设置页。步骤 4 是流程终点占位（清单确认后结束）。</para>
///
/// <para><b>M4 进度</b>：步骤 1 的读包链路已接通（<see cref="HandlePackChosenAsync"/>），
/// 步骤 2 的三张卡（整合包 / 目标 / 更新范围）全部填充；
/// 「开始对比」待 M5 接 <see cref="McUpdater.Core.DiffEngine"/>。</para>
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // M1 不启用标题栏内嵌：像素风顶栏自己画了 3px 硬边底栏，
        // 系统标题栏叠在上面会显得割裂。等 M5 视觉定稿再决定是否内嵌。
        StepBarControl.SetCurrent(1);
        StepBarControl.StepClicked += OnStepClicked;

        // Window 自身没有 Loaded 事件（那是 FrameworkElement 的），
        // 挂在根 Grid 上 —— 它的 Loaded 在整棵视觉树就绪后触发。
        RootGrid.Loaded += RootGrid_Loaded;
    }

    /// <summary>视觉树就绪后启动背景动画（M0 探针③ 的结论：不占 UI 线程）。</summary>
    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        Background.Start();
    }

    /// <summary>
    /// 当前整合包句柄。必须持有到 M5 —— diff 阶段要从里面读 jar 内容。
    ///
    /// <para><b>换包时必须 Dispose 旧的</b>：PCL2 快速路径下句柄持有
    /// <see cref="ZipReader"/>（包着 FileStream）。不释放的话每次换包都漏一个
    /// 文件句柄，且临时缓存文件删不掉。</para>
    /// </summary>
    private ModpackHandle? _pack;

    /// <summary>
    /// 用户勾选要更新的分类（M3）。
    /// 「开始对比」时按这里的顺序做 diff，空集合则禁止开始。
    /// </summary>
    private readonly HashSet<string> _scope = new(StringComparer.Ordinal);

    /// <summary>
    /// 当前选中的目标版本（M4）。null = 没扫描到 / 没匹配上，
    /// 此时目标卡显示「没能自动认出该更新哪个版本」，且「开始对比」禁用。
    /// </summary>
    private ClientVersion? _target;

    /// <summary>
    /// 上次扫描到的全部候选版本（按匹配度降序）。
    /// </summary>
    private List<ClientVersion> _candidates = [];

    // ------------------------------------------------------------------
    // 步骤 3：变更清单（M5）
    // ------------------------------------------------------------------

    /// <summary>上次的对比结果。</summary>
    private DiffPlan? _diff;

    /// <summary>由 <see cref="DiffPlan"/> 展开出的行视图（全量，未过滤）。</summary>
    private List<ChangeRowView> _rows = [];

    /// <summary>用户勾选的变更（<c>分类::key</c>）。</summary>
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);

    /// <summary>当前筛选：all / updated / added / removed / unchanged。</summary>
    private string _filter = "all";

    /// <summary>对比进行中。防连点。</summary>
    private bool _diffRunning;

    /// <summary>
    /// 丢弃对比结果。换包、换目标时必须调 —— 旧的变更清单指向的是旧组合，
    /// 留着就是一份会误导人的清单。
    /// </summary>
    private void ClearDiff()
    {
        _diff = null;
        _rows = [];
        _selected.Clear();
        _filter = "all";
    }

    // ------------------------------------------------------------------
    // 步骤切换
    // ------------------------------------------------------------------

    private void OnStepClicked(int step)
    {
        // 步骤条只允许回退到已到达过的步（StepBar 内部已禁用未到达的步）。
        switch (step)
        {
            case 1:
                ShowPanel(1);
                break;
            case 2:
                // 没选包就不该出现空白面板 —— 给明确提示而不是让它空着
                if (_pack is null)
                {
                    SetHint("还没选整合包", isError: true);
                    return;
                }
                ShowPanel(2);
                break;
            case 3:
                // 变更清单依赖一次对比结果；没对比过就回步骤 2，不给空白面板
                if (_diff is null)
                {
                    SetHint("还没有对比过，先在步骤 2 点「开始对比」", isError: true);
                    return;
                }
                ShowPanel(3);
                break;
        }
    }

    private void ShowPanel(int step)
    {
        DropZone.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        HintText.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Panel2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Panel3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        StepBarControl.SetCurrent(step);
    }

    // ------------------------------------------------------------------
    // 拖放（CSS .dropzone 的 is-over 态；整块不再可点，只有按钮能选文件）
    // ------------------------------------------------------------------

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        SetDropOver(true);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        // DragOver 高频触发，必须在这里 Accepted，否则系统判定为未接受而拒绝放下
        e.AcceptedOperation = HasPackFile(e.DataView) ? DataPackageOperation.Copy
                                                     : DataPackageOperation.None;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        SetDropOver(false);
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        SetDropOver(false);

        if (!HasPackFile(e.DataView))
        {
            SetHint("请拖入 .zip 或 .mrpack 文件（不支持文件夹）", isError: true);
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count == 0 || items[0] is not IStorageFile file)
        {
            SetHint("没有读到文件", isError: true);
            return;
        }

        await HandlePackChosenAsync(file.Path);
    }

    private static bool HasPackFile(DataPackageView view)
    {
        if (!view.Contains(StandardDataFormats.StorageItems))
        {
            return false;
        }
        // 详细类型判断要异步，这里只做快速筛选：文件名后缀。
        // 完整校验在读取阶段（ModpackReader）再做一次。
        return true;
    }

    private void SetDropOver(bool over)
    {
        // CSS .dropzone.is-over：边框转草绿 + 背景转草绿光晕
        DropZone.BorderBrush = over
            ? (Brush)Application.Current.Resources["BrushGrassD"]
            : (Brush)Application.Current.Resources["BrushDirt"];
        DropZone.Background = over
            ? (Brush)Application.Current.Resources["McGrassGlow"]
            : (Brush)Application.Current.Resources["BrushTransparent"];
    }

    // ------------------------------------------------------------------
    // 选择整合包
    // ------------------------------------------------------------------

    private async void OnPickFileClick(object sender, RoutedEventArgs e)
        => await PickAndLoadAsync();

    /// <summary>
    /// 打开文件选择器。唯一入口 —— 拖放区整块已不再挂点击事件，
    /// 只有「浏览文件…」按钮的 <see cref="OnPickFileClick"/> 走这里。
    ///
    /// 【重入锁为什么必要】
    /// 防的是「连点两下按钮，弹出两个模态窗口」。
    /// 早期版本的根因是另一回事：拖放区 Border 挂了 Tapped 而按钮挂了 Click，
    /// 点一下会触发两次（WinUI 的 Tapped 会冒泡）。**现已通过移除 Tapped
    /// 从根本上消除了那个入口**，不再需要判断 OriginalSource 属于谁。
    /// 但锁仍然保留 —— 它防的是连点这类与入口数量无关的情况，
    /// 且成本只有三行，把"最多一个模态窗"从"依赖调用方正确"变成方法自身的保证。
    ///
    /// 锁必须覆盖 await 期间：<c>PickSingleFileAsync</c> 会把控制权交回
    /// 事件循环，那段时间完全可能进来第二次点击。
    /// </summary>
    private bool _pickerOpen;

    /// <summary>读包进行中。防止连点「换一个」时并发开两个包。</summary>
    private bool _loading;

    private async Task PickAndLoadAsync()
    {
        if (_pickerOpen)
        {
            return;
        }

        _pickerOpen = true;
        try
        {
            // WindowHandle 在 App.OnLaunched 里通过 WindowNative 取得 ——
            // unpackaged 下传统 WinRT Picker 必须用它初始化，否则弹不出来。
            var path = await Services.PickerHelper.PickModpackAsync(App.WindowHandle);
            if (path is null)
            {
                return; // 用户取消
            }

            await HandlePackChosenAsync(path);
        }
        catch (Exception ex)
        {
            SetHint($"打开失败：{ex.Message}", isError: true);
        }
        finally
        {
            _pickerOpen = false;
        }
    }

    /// <summary>
    /// 选定整合包后的完整处理：校验 → 遮罩进度读包 → 填步骤 2 卡片。
    ///
    /// <para><b>为什么没有 Node 那道 512MB 上限</b>
    /// Node 有这道检查，是因为它把包通过 HTTP <c>POST</c> 上传到本地服务
    /// （<c>API.uploadPack</c>），超出默认 body 上限就会失败。
    /// 本机 WinUI 直接读本地路径，不经过任何上传环节，照搬就是凭空设限。
    ///
    /// <b>但包里仍有一层真实的size 防线</b>：<c>AppConfig.InMemoryThreshold</c>
    /// —— 超过 128MB 的内层包走「落缓存临时文件」而不是进内存。
    /// 那是移植时就有的保护，不是 UI 层该重复的检查。</para>
    ///
    /// <para><b>遮罩与 _loading 必须由 finally 无条件收尾</b>
    /// —— 这条是被真实 bug 教出来的：读包成功后紧接着执行
    /// <see cref="RenderScope"/>，那里一旦抛异常（首次就撞到过
    /// <c>InvalidCastException</c>），异常会越过读包那个 try 传播到
    /// 调用方，而此时遮罩还开着。<b>症状是进度条永远停在 Core 报的最后一帧
    /// 35%，整个窗口点不动</b>，而真实错误只写进了被遮罩盖住的 HintText，
    /// 看起来像「读包卡死」。凡是「开遮罩 → 干活 → 关遮罩」，
    /// 关遮罩必须在 finally 里，否则任何一个后置异常都会把它变成死锁。</para>
    /// </summary>
    private async Task HandlePackChosenAsync(string path)
    {
        if (_loading)
        {
            return;
        }

        // ---- 校验（对齐 Node app.js handleFile 的两道前置检查）----
        var lower = Path.GetFileName(path).ToLowerInvariant();
        if (!lower.EndsWith(".zip", StringComparison.Ordinal) &&
            !lower.EndsWith(".mrpack", StringComparison.Ordinal))
        {
            SetHint($"不支持的文件类型：{Path.GetFileName(path)}（请选择 .zip 或 .mrpack）", isError: true);
            return;
        }

        if (!File.Exists(path))
        {
            SetHint($"文件不存在：{path}", isError: true);
            return;
        }

        _loading = true;
        ShowOverlay("正在读取整合包", "正在解析整合包…", indeterminate: true);

        try
        {
            // Node 版的 512MB 上限是为了 HTTP 上传（本机 WinUI 直接读本地路径，
            // 没有上传环节），所以这里**不设大小上限** —— 见上方注释。
            var handle = await Services.PackLoadService.OpenAsync(
                path, RootGrid.DispatcherQueue,
                (msg, pct) => ShowOverlayProgress(msg, pct));

            // 换包：释放旧句柄（否则漏 FileStream 与临时缓存文件）
            _pack?.Dispose();
            _pack = handle;

            // 目标必须跟着换 —— 旧目标是按上一个包匹配出来的，
            // 留着会让卡片显示一个与当前包毫无关系的版本。
            _target = null;
            _candidates = [];
            ClearDiff();

            RenderPackInfo(handle);
            RenderScope(handle);

            // M4：目标扫描放在读包之后、补 100% 之前。
            // 它要走盘（枚举 versions/、读 Setup.ini、扫 mods 目录计数），
            // 虽然只读但可能上百毫秒，必须 Task.Run 出去，不能堵 UI 线程。
            ShowOverlayProgress("正在扫描游戏目录…", 70);
            await ResolveTargetAsync(handle);

            // 【补100% 这一帧】ModpackReader 的进度刻度只到 35
            // （Node: 5→15→25→30→35），100% 由 UI 层自己补——
            // Node 侧对应 `overlay.progress('完成', 100)`（public/js/app.js）。
            // 不补的话读包结束时进度条会停在 35% 然后突然消失。
            ShowOverlayProgress("整合包读取完成", 100);

            ShowPanel(2);
            SetHint($"已读取 {Path.GetFileName(path)}", isError: false);
        }
        catch (Exception ex)
        {
            SetHint(ex.Message, isError: true);
        }
        finally
        {
            // 无条件收尾：见方法注释里那段「为什么必须在 finally」
            HideOverlay();
            _loading = false;
        }
    }

    /// <summary>
    /// 把整合包信息填进步骤 2 的卡片。
    ///
    /// <para><b>取值规则全在 <see cref="PackInfoMapper"/> 里</b>，本方法只负责
    /// 把算好的值写进控件。这样"包名取 PackName 还是文件名"、"空分类怎么显示"
    /// 这些保真规则可以被单元测试直接断言，不必靠看界面来判断。</para>
    /// </summary>
    private void RenderPackInfo(ModpackHandle handle)
    {
        var info = PackInfoMapper.Build(handle, Format.ShortPath, Format.Bytes);

        PackNameText.Text = info.Name;
        GameVersionText.Text = info.GameVersion;

        if (info.LoaderTag is null)
        {
            LoaderTag.Visibility = Visibility.Collapsed;
        }
        else
        {
            LoaderTag.Visibility = Visibility.Visible;
            LoaderTagText.Text = info.LoaderTag;
        }

        CategoryTags.Items.Clear();
        if (info.IsEmptyCategories)
        {
            // Node: `|| '<span class="muted">空</span>'`
            CategoryTags.Items.Add(new TextBlock
            {
                Text = "空",
                FontSize = (double)Application.Current.Resources["FontSizeSmall"],
                Foreground = (Brush)Application.Current.Resources["BrushMuted"],
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else
        {
            foreach (var t in info.Categories)
            {
                CategoryTags.Items.Add(MakeTag($"{t.Title} {t.Count}", tagStyle: null));
            }
        }

        PackPathText.Text = info.FileLine;
    }

    /// <summary>
    /// 渲染更新范围勾选项（规则见 <see cref="PackInfoMapper.BuildScope"/>）。
    /// </summary>
    private void RenderScope(ModpackHandle handle)
    {
        _scope.Clear();
        ScopeGrid.Children.Clear();

        foreach (var view in PackInfoMapper.BuildScope(handle))
        {
            if (view.DefaultOn)
            {
                _scope.Add(view.Key);
            }

            var toggle = MakeScopeToggle(view);
            toggle.IsChecked = view.DefaultOn;
            toggle.IsEnabled = !view.IsEmpty;

            // 四列固定布局：列号即分类在 ManagedCategories 里的下标
            Grid.SetColumn(toggle, ScopeGrid.Children.Count % 4);
            ScopeGrid.Children.Add(toggle);
        }

        UpdateDiffButtonState();
    }

    // ------------------------------------------------------------------
    // 目标版本（M4）
    // ------------------------------------------------------------------

    /// <summary>
    /// 扫描本机所有游戏版本并给整合包挑一个目标（M4）。
    ///
    /// <para><b>为什么要 <c>Task.Run</c></b>：<see cref="Pcl2Detector.Detect"/> 会
    /// 读 <c>PCL/Log1~5.txt</c>（每个可能几 MB）、枚举各盘常见目录，
    /// <see cref="ClientScanner.ScanAll"/> 还要给每个版本数一遍 mods。
    /// 实测本机 3 个版本约几十毫秒，但别人机器上十几个文件夹就是几百毫秒 ——
    /// 这段同步 IO 绝不能放在 UI 线程上。</para>
    ///
    /// <para><b>扫描失败不能让选包整体失败</b>：找不到 PCL2 是常见情况
    /// （玩家用别的启动器），此时应当照常进入步骤 2，
    /// 由目标卡提示「手动选择」，而不是弹一个错误把用户挡回步骤 1。</para>
    /// </summary>
    private async Task ResolveTargetAsync(ModpackHandle handle)
    {
        ClientVersion? best = null;
        List<ClientVersion> ranked = [];

        try
        {
            await Task.Run(() =>
            {
                var detected = Pcl2Detector.Detect();
                var all = ClientScanner.ScanAll(detected.Folders);
                var (b, r) = ClientScanner.MatchTarget(
                    all, handle.VersionNameHint, handle.GameVersion, handle.Loader);
                best = b;
                ranked = r;
            });
        }
        catch (Exception ex)
        {
            // 扫描是"锦上添花"：失败只记提示，包照样能用，用户还能手动选。
            SetHint($"扫描游戏目录时出错，可手动选择目标：{ex.Message}", isError: true);
        }

        _target = best;
        _candidates = ranked;
        RenderTarget(handle);
    }

    /// <summary>把目标版本填进步骤 2 的目标卡（取值规则见 <see cref="TargetMapper"/>）。</summary>
    private void RenderTarget(ModpackHandle? handle)
    {
        var warnings = TargetMapper.BuildWarnings(handle, _target);
        var card = TargetMapper.Build(_target, warnings, Format.ShortPath);

        TargetMissingPanel.Visibility = card.HasTarget ? Visibility.Collapsed : Visibility.Visible;
        TargetMatchedPanel.Visibility = card.HasTarget ? Visibility.Visible : Visibility.Collapsed;
        if (!card.HasTarget)
        {
            UpdateDiffButtonState();
            return;
        }

        TargetNameText.Text = card.Name;
        TargetFolderText.Text = string.IsNullOrEmpty(card.FolderName) ? "" : $"（{card.FolderName}）";
        TargetModCountText.Text = $"{card.ModCount} 个";
        TargetPathText.Text = card.DirLine;

        TargetTags.Items.Clear();
        foreach (var tag in card.Tags)
        {
            TargetTags.Items.Add(MakeTag(tag.Text, tag.StyleKey));
        }

        TargetWarnings.Items.Clear();
        foreach (var w in card.Warnings)
        {
            TargetWarnings.Items.Add(new TextBlock
            {
                Text = "· " + w,
                FontSize = (double)Application.Current.Resources["FontSizeSmall"],
                Foreground = (Brush)Application.Current.Resources["BrushInk2"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 2),
            });
        }
        TargetWarningsBox.Visibility = card.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        TargetWhyText.Text = card.WhyLine;
        TargetWhyText.Visibility = card.WhyLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateDiffButtonState();
    }

    /// <summary>
    /// 自检入口：按真实用户路径把步骤 2 整个面板渲染出来（<c>--selftest</c> 用）。
    ///
    /// <para><b>为什么值得为测试开这个口子</b>：M3 最贵的一个 bug 就是
    /// 「数据映射全对、256 个测试全绿，但一行控件强转让界面整个不可用」。
    /// <see cref="TargetMapper"/> 的输出正确，不代表 <c>TargetNameText</c>
    /// 这些 <c>x:Name</c> 真的存在 —— 拼错一个名字要到运行时才炸，
    /// 而炸的地方在读包之后，表现是进度条卡住、窗口点不动。
    /// 自检里真渲染一遍，把这类问题提前到命令行就能看出来。</para>
    ///
    /// <para><b>顺带服务 <c>--show</c> 截图模式</b>：自检跑完调这个方法，
    /// 窗口上就是和真实使用完全一致的画面（整合包卡 + 目标卡 + 范围勾选），
    /// 整屏抓一张就有视觉证据，不必靠人肉点一遍。</para>
    /// </summary>
    internal void RenderStep2ForSelfTest(ModpackHandle handle, ClientVersion? target)
    {
        _target = target;
        RenderPackInfo(handle);
        RenderScope(handle);
        RenderTarget(handle);
        ShowPanel(2);
    }

    /// <summary>
    /// 自检入口：模拟「手动选择」指定了一个目标（<c>--target</c> 截图用）。
    /// 与 <see cref="OnChangeTargetClick"/> 走同一条路，只是不弹选择器。
    ///
    /// <para><b>必须显式切回步骤 2</b>：目标卡长在步骤 2 的面板上，
    /// 而 <see cref="RenderTarget"/> 只改控件值、不改面板可见性 ——
    /// 第一版漏了这行，截图模式跑出来还是步骤 3 的画面。</para>
    /// </summary>
    internal void SetTargetForSelfTest(ClientVersion target)
    {
        _target = target;
        ClearDiff();
        RenderTarget(_pack);
        ShowPanel(2);
    }

    private void UpdateDiffButtonState()
        => StartDiffButton.IsEnabled = _scope.Count > 0 && _target is not null;

    // ------------------------------------------------------------------
    // 步骤 2 的操作
    // ------------------------------------------------------------------

    /// <summary>「换一个」：回步骤 1 重新选包。旧句柄保留到新包读成功才释放。</summary>
    private void OnChangePackClick(object sender, RoutedEventArgs e)
    {
        ShowPanel(1);
        SetHint("选好新的整合包后点「浏览文件…」或直接拖进来", isError: false);
    }

    /// <summary>
    /// 「手动选择」：用户自己指定一个游戏版本目录（M4）。
    ///
    /// <para><b>为什么只做「浏览文件夹」这一条路</b>：Node 版还有一条
    /// 「候选列表弹窗」，但那需要一整套模态列表控件。
    /// 自动匹配已经覆盖了绝大多数场景（版本名/游戏版本/加载器三重对齐），
    /// 手动指定是兜底，一个文件夹选择器足够把流程走通。
    /// 候选列表等 M5 做变更清单时一并补 —— 那时才真正需要"切换目标重新对比"。</para>
    /// </summary>
    private async void OnChangeTargetClick(object sender, RoutedEventArgs e)
    {
        if (_pack is null)
        {
            SetHint("先选一个整合包", isError: true);
            return;
        }

        var dir = await Services.PickerHelper.PickVersionFolderAsync(App.WindowHandle);
        if (dir is null)
        {
            return; // 用户取消
        }

        try
        {
            // describeDir 的报错文案是给用户看的（"这个目录里没有 mods 文件夹…"），
            // 直接冒泡到提示行，不要自己再包一层。
            _target = ClientScanner.DescribeDir(dir);
            _candidates = [_target];
            // 目标变了，上次的对比结果指向的是旧目录 —— 留着会让用户
            // 照着一份错误的清单去操作。
            ClearDiff();
            RenderTarget(_pack);
            SetHint($"目标版本已切换到 {_target.Name}", isError: false);
        }
        catch (Exception ex)
        {
            SetHint(ex.Message, isError: false);
        }
    }

    private void OnBackToStep1Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(1);
        SetHint("", isError: false);
    }

    /// <summary>
    /// 「开始对比」：跑 <see cref="DiffPlanner"/> 并渲染步骤 3（M5）。
    ///
    /// <para><b>进度回调必须 marshal 回 UI 线程</b>：diff 跑在 <c>Task.Run</c> 里，
    /// <see cref="ShowOverlayProgress"/> 改的是控件属性。
    /// 跨线程直接调会在 WinUI 上抛 <c>COMException</c>（RPC_E_WRONG_THREAD），
    /// 而且抛在后台线程上 —— 它不会被这里的 catch 抓到，只会让窗口静默卡住。
    /// 与 <c>PackLoadService.OpenAsync</c> 同一个处理。</para>
    ///
    /// <para><b>不改用户的文件</b>：本次范围不含应用更新，
    /// 所以 <see cref="DiffPlanner"/> 刻意不落 CRC 缓存文件。</para>
    /// </summary>
    private async void OnStartDiffClick(object sender, RoutedEventArgs e)
    {
        if (_pack is null || _target is null)
        {
            SetHint("先选好整合包和目标版本", isError: true);
            return;
        }
        if (_diffRunning)
        {
            return;
        }

        _diffRunning = true;
        ShowOverlay("正在对比", "正在建立连接…", indeterminate: true);

        try
        {
            var scope = _scope.ToList();
            var pack = _pack;
            var contentDir = _target.ContentDir;

            var plan = await Task.Run(() => DiffPlanner.Run(pack, contentDir, scope,
                (msg, pct) => RootGrid.DispatcherQueue.TryEnqueue(() => ShowOverlayProgress(msg, pct))));

            _diff = plan;
            _rows = ChangeList.BuildRows(plan.Changes);
            _selected.Clear();
            foreach (var k in ChangeList.RecommendedKeys(plan.Changes))
            {
                _selected.Add(k);
            }
            _filter = "all";
            SearchBox.Text = "";

            ShowOverlayProgress("对比完成", 100);
            RenderStep3();
            ShowPanel(3);
            SetHint($"对比完成：新增 {plan.Summary.Added} / 更新 {plan.Summary.Updated} / " +
                    $"移除 {plan.Summary.Removed}，写入约 {plan.Summary.BytesText}", isError: false);
        }
        catch (Exception ex)
        {
            SetHint($"对比失败：{ex.Message}", isError: true);
        }
        finally
        {
            HideOverlay();
            _diffRunning = false;
        }
    }

    /// <summary>步骤 3 全量渲染（Node <c>renderStep3</c> 的四段）。</summary>
    internal void RenderStep3()
    {
        RenderNotices();
        RenderFilters();
        RenderChanges();
        RenderStatus();
    }

    /// <summary>
    /// 自检入口：按一次真实对比结果把步骤 3 渲染出来（<c>--selftest</c> 用）。
    /// 理由同 <see cref="RenderStep2ForSelfTest"/>：
    /// 「数据对了」不等于「控件名对了、行构造得出来」。
    /// </summary>
    internal void RenderStep3ForSelfTest(DiffPlan plan)
    {
        _diff = plan;
        _rows = ChangeList.BuildRows(plan.Changes);
        _selected.Clear();
        foreach (var k in ChangeList.RecommendedKeys(plan.Changes))
        {
            _selected.Add(k);
        }
        _filter = "all";
        RenderStep3();
        ShowPanel(3);
    }

    // ---- 提示框（Node renderNotices）----

    private void RenderNotices()
    {
        NoticeBox.Children.Clear();
        if (_diff is null)
        {
            return;
        }

        var s = _diff.Summary;

        // 1) 重复模组（danger）
        if (_diff.Conflicts.Count > 0)
        {
            var fromPack = _diff.Conflicts.Count(c => c.Side != "local");
            var box = MakeNotice("PixelNoticeDangerStyle", "BrushRed",
                $"发现 {_diff.Conflicts.Count} 处重复模组");

            AddNoticeText(box, "同一个模组装了多份，Fabric / Forge 启动时会直接报错退出。");
            foreach (var c in _diff.Conflicts)
            {
                AddNoticeText(box, $"· {WhereText(c.Side)}有 {c.Files.Count} 份 {c.DisplayName}：" +
                                   string.Join(" ", c.Files.Select(f => f.FileName)), mono: true);
            }
            if (fromPack > 0)
            {
                AddNoticeText(box, $"其中 {fromPack} 处是整合包自带的 —— 这是打包时的疏漏，" +
                                   "你在本地删掉多余的那份，下次更新还会被装回来。" +
                                   "建议把这条截图发给服主，让他重新打包。");
            }
            NoticeBox.Children.Add(box);
        }

        // 2) 玩家自装的移除项（warn）
        if (s.PlayerInstalledRemovals > 0)
        {
            var box = MakeNotice("PixelNoticeWarnStyle", "TagAmberInk",
                $"有 {s.PlayerInstalledRemovals} 个你自己装的模组不在整合包里");
            AddNoticeText(box, "这些默认不会被删除，标了「你装的」。如果想跟整合包完全保持一致，手动勾上它们。");
            NoticeBox.Children.Add(box);
        }

        // 3) 首次运行（info）—— 只在真有移除项时才提示，否则是噪音
        if (s.FirstRun && s.Removed > 0)
        {
            var box = MakeNotice("PixelNoticeInfoStyle", "TagBlueInk", "这是第一次用本工具更新这个版本");
            AddNoticeText(box, "还没有安装记录，分不清哪些是你自己装的，所以移除项默认全都不勾。" +
                               "请自己过一遍，确认哪些该删。这次更新之后就会有记录，下次会自动判断。");
            NoticeBox.Children.Add(box);
        }

        // 4) 被跳过的文件（info）
        if (s.Skipped.Protected + s.Skipped.Oversize > 0)
        {
            var box = MakeNotice("PixelNoticeInfoStyle", "TagBlueInk",
                $"已跳过 {s.Skipped.Protected + s.Skipped.Oversize} 个文件，不参与更新");
            AddNoticeText(box, "配置目录里混着模组运行时生成的个人数据——存档备份、物品查询历史、" +
                               "语音设备设置、缓存等。这些不会被列出，也不会被修改或删除。");
            if (s.Skipped.Samples.Count > 0)
            {
                AddNoticeText(box, "例如：" + string.Join("  ", s.Skipped.Samples.Take(4)), mono: true);
            }
            NoticeBox.Children.Add(box);
        }
    }

    private static string WhereText(string side) => side switch
    {
        "zip" => "整合包里",
        "local" => "你的游戏里",
        "both" => "整合包和你的游戏里都",
        _ => "",
    };

    private Border MakeNotice(string styleKey, string inkKey, string title)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = Br(inkKey),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 3),
        });

        var box = new Border
        {
            Style = (Style)Application.Current.Resources[styleKey],
            Child = panel,
            Tag = panel,      // 供 AddNoticeText 找到正文容器
        };
        return box;
    }

    private static void AddNoticeText(Border notice, string text, bool mono = false)
    {
        if (notice.Tag is not StackPanel panel)
        {
            return;
        }
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            Foreground = (Brush)Application.Current.Resources["BrushInk"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        // 【坑，自检实测抓到】FontFamily 不能赋 null —— 那会被当成字符串
        // "Unknown"，渲染时抛 COMException"'Unknown' is not a valid value for
        // property 'FontFamily'"，且只在控件真正进入视觉树时才炸。
        // 要"用默认字体"就只能不设这个属性。
        if (mono)
        {
            tb.FontFamily = (FontFamily)Application.Current.Resources["FontMono"];
        }
        panel.Children.Add(tb);
    }

    // ---- 筛选芯片（Node renderFilters）----

    private void RenderFilters()
    {
        FilterChips.Children.Clear();
        if (_diff is null)
        {
            return;
        }

        var s = _diff.Summary;
        // 【保真】顺序固定：全部 → 更新 → 新增 → 移除 → 无变化（与 Node 的
        // #filters 里五个 button 的顺序一致）
        (string Key, string Label, int Count)[] chips =
        [
            ("all", "全部", s.Changed),
            ("updated", "更新", s.Updated),
            ("added", "新增", s.Added),
            ("removed", "移除", s.Removed),
            ("unchanged", "无变化", s.Unchanged),
        ];

        foreach (var chip in chips)
        {
            var btn = new ToggleButton
            {
                Content = chip.Key == "all" ? "全部" : $"{chip.Label} {chip.Count}",
                Style = (Style)Application.Current.Resources["PixelChipStyle"],
                IsChecked = _filter == chip.Key,
                Tag = chip.Key,
            };
            btn.Click += OnFilterChipClick;
            FilterChips.Children.Add(btn);
        }
    }

    private void OnFilterChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string key } btn)
        {
            _filter = key;
            btn.IsChecked = true;                       // 点已选中的芯片时不反选
            RenderFilters();
            RenderChanges();
        }
    }

    // ---- 变更列表（Node renderChanges）----

    private void RenderChanges()
    {
        ChangesList.Children.Clear();

        var visible = ChangeList.Visible(_rows, _filter, SearchBox.Text);
        if (visible.Count == 0)
        {
            var s = _diff?.Summary;
            ChangesList.Children.Add(new TextBlock
            {
                Text = s is not null && s.Changed == 0 && _filter == "all"
                    ? "你的版本和整合包完全一致，不需要更新"
                    : "没有符合条件的项目",
                FontSize = 13,
                Foreground = Br("BrushMuted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 46, 0, 46),
            });
            return;
        }

        foreach (var group in ChangeList.Group(visible))
        {
            ChangesList.Children.Add(MakeGroupHead(group));
            foreach (var row in group.Rows)
            {
                ChangesList.Children.Add(MakeChangeRow(row));
            }
        }
    }

    private Border MakeGroupHead(ChangeGroupView group)
    {
        var grid = new Grid
        {
            Style = (Style)Application.Current.Resources["PixelGroupHeadStyle"],
            Padding = new Thickness(16, 9, 16, 9),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock
        {
            Text = group.Title,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.ExtraBold,
            Foreground = Br("BrushInk"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        var count = new TextBlock
        {
            Text = $"{group.Rows.Count} 项",
            FontSize = 12,
            Foreground = Br("BrushMuted"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(count, 1);
        grid.Children.Add(count);

        if (group.HasSelectable)
        {
            var all = new Button
            {
                Content = "全选 / 全不选",
                Style = (Style)Application.Current.Resources["PixelLinkButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Right,
                Tag = group.Key,
            };
            all.Click += OnToggleGroupClick;
            Grid.SetColumn(all, 2);
            grid.Children.Add(all);
        }

        return new Border
        {
            Child = grid,
            BorderBrush = Br("BrushLineSoft"),
            BorderThickness = new Thickness(0, 0, 0, 2),
        };
    }

    private void OnToggleGroupClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string cat })
        {
            return;
        }
        // 【保真】判据是"当前可见且属于该分类的可勾选项"是否已全勾
        var items = ChangeList.Visible(_rows, _filter, SearchBox.Text)
            .Where(r => r.Item.Category == cat && r.CanCheck)
            .ToList();
        var allOn = items.All(r => _selected.Contains(r.SelKey));
        foreach (var r in items)
        {
            if (allOn) { _selected.Remove(r.SelKey); } else { _selected.Add(r.SelKey); }
        }
        RenderChanges();
        RenderStatus();
    }

    // ---- 变更行（Node rowHtml）----

    private Border MakeChangeRow(ChangeRowView row)
    {
        var on = _selected.Contains(row.SelKey);

        var grid = new Grid { Padding = new Thickness(16, 10, 16, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });

        // 勾选框
        var check = MakeCheckBox(row.SelKey, on, row.CanCheck);
        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        // 类型徽章（CSS .row-badge 5x30）
        var badge = new Border
        {
            Style = (Style)Application.Current.Resources["ChangeRowBadgeStyle"],
            Background = Br(row.Item.Type switch
            {
                ChangeType.Updated => "BrushBlue",
                ChangeType.Added => "BrushGrass",
                ChangeType.Removed => "BrushRed",
                _ => "BrushCard3",
            }),
            BorderBrush = Br("BrushEdge"),
            BorderThickness = new Thickness(1.5),
        };
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);

        // 名称 + 标签 + 文件名
        var main = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var nameText = new TextBlock
        {
            Text = row.DisplayName,
            FontSize = 13.5,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            // CSS .row.is-unchecked .row-name → color: ink-2
            Foreground = Br(on ? "BrushInk" : "BrushInk2"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        nameRow.Children.Add(nameText);
        foreach (var tag in row.Tags)
        {
            var el = MakeTag(tag.Text, tag.StyleKey);
            if (tag.Tooltip is not null)
            {
                ToolTipService.SetToolTip(el, tag.Tooltip);
            }
            nameRow.Children.Add(el);
        }
        main.Children.Add(nameRow);
        main.Children.Add(new TextBlock
        {
            Text = row.FileName,
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.Resources["FontMono"],
            Foreground = Br("BrushMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0),
        });
        Grid.SetColumn(main, 2);
        grid.Children.Add(main);

        // 版本（CSS .row-ver）
        var ver = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        if (row.Item.Type == ChangeType.Updated)
        {
            ver.Children.Add(MakeVerText(row.OldVersion ?? "?", "BrushMuted", strike: true));
            ver.Children.Add(MakeVerText("→", "BrushDirt", strike: false));
            ver.Children.Add(MakeVerText(row.NewVersion ?? "?", "BrushGrassDD", strike: false, bold: true));
        }
        else if (row.Item.Type == ChangeType.Added)
        {
            ver.Children.Add(MakeVerText(row.NewVersion ?? "", "BrushGrassDD", strike: false, bold: true));
        }
        else if (row.Item.Type == ChangeType.Removed)
        {
            ver.Children.Add(MakeVerText(row.OldVersion ?? "", "BrushMuted", strike: true));
        }
        else
        {
            ver.Children.Add(MakeVerText(row.NewVersion ?? "", "BrushMuted", strike: false));
        }
        Grid.SetColumn(ver, 3);
        grid.Children.Add(ver);

        // 大小（CSS .row-size width 68 right）
        var size = new TextBlock
        {
            Text = row.SizeText,
            FontSize = 12,
            Foreground = Br("BrushMuted"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(size, 4);
        grid.Children.Add(size);

        var border = new Border
        {
            Child = grid,
            BorderBrush = Br("BrushLineSoft"),
            BorderThickness = new Thickness(0, 0, 0, 2),
            // CSS .row.is-unchanged → opacity .6
            Opacity = row.Item.Type == ChangeType.Unchanged ? 0.6 : 1.0,
        };
        return border;
    }

    private static TextBlock MakeVerText(string text, string brushKey, bool strike, bool bold = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            Foreground = Br(brushKey),
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.ExtraBold : Microsoft.UI.Text.FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (strike)
        {
            tb.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
        }
        return tb;
    }

    /// <summary>
    /// 17x17 勾选框（与更新范围那套同一造型，但用无外观模板，
    /// 避免 WinUI 默认样式在悬停时糊一层系统灰）。
    /// </summary>
    private ToggleButton MakeCheckBox(string selKey, bool on, bool enabled)
    {
        var check = new Border
        {
            Width = 4, Height = 9,
            CornerRadius = new CornerRadius(1),
            Background = Br("BrushGrassInk"),
            RenderTransform = new RotateTransform { Angle = 42, CenterX = 2, CenterY = 4.5 },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -2, 0, 0),
            Visibility = on ? Visibility.Visible : Visibility.Collapsed,
        };

        var box = new Grid { Width = 17, Height = 17 };
        box.Children.Add(new Border
        {
            Width = 17, Height = 17,
            CornerRadius = new CornerRadius(3),
            Background = Br("BrushCard"),
            BorderBrush = Br("BrushEdge"),
            BorderThickness = new Thickness(2.5),
        });
        box.Children.Add(check);

        var toggle = new ToggleButton
        {
            Style = (Style)Application.Current.Resources["PixelBareToggleStyle"],
            Content = box,
            IsChecked = on,
            IsEnabled = enabled,
            Tag = selKey,
            Width = 28, Height = 30,
        };
        void Sync() => check.Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        toggle.Checked += (_, _) => Sync();
        toggle.Unchecked += (_, _) => Sync();
        toggle.Click += OnChangeRowToggleClick;
        return toggle;
    }

    private void OnChangeRowToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string key } toggle || !toggle.IsEnabled)
        {
            return;
        }
        if (toggle.IsChecked == true) { _selected.Add(key); } else { _selected.Remove(key); }
        RenderChanges();
        RenderStatus();
    }

    // ---- 底部统计（Node renderStatus）----

    private void RenderStatus()
    {
        StatusLeft.Children.Clear();
        var st = ChangeList.Status(_rows.Select(r => r.Item), _selected);

        if (st.Count == 0)
        {
            StatusLeft.Children.Add(MakeStatusText("还没有勾选任何变更", "BrushMuted", false));
            return;
        }

        StatusLeft.Children.Add(MakeStatusText("已勾选 ", "BrushInk2", false));
        StatusLeft.Children.Add(MakeStatusText(st.Count.ToString(CultureInfo.InvariantCulture), "BrushInk", true));
        StatusLeft.Children.Add(MakeSep());
        StatusLeft.Children.Add(MakeStatusText("新增 ", "BrushInk2", false));
        StatusLeft.Children.Add(MakeStatusText(st.Added.ToString(CultureInfo.InvariantCulture), "BrushInk", true));
        StatusLeft.Children.Add(MakeSep());
        StatusLeft.Children.Add(MakeStatusText("更新 ", "BrushInk2", false));
        StatusLeft.Children.Add(MakeStatusText(st.Updated.ToString(CultureInfo.InvariantCulture), "BrushInk", true));
        StatusLeft.Children.Add(MakeSep());
        StatusLeft.Children.Add(MakeStatusText("移除 ", "BrushInk2", false));
        StatusLeft.Children.Add(MakeStatusText(st.Removed.ToString(CultureInfo.InvariantCulture), "BrushInk", true));
        StatusLeft.Children.Add(MakeSep());
        StatusLeft.Children.Add(MakeStatusText("写入约 ", "BrushInk2", false));
        StatusLeft.Children.Add(MakeStatusText(st.BytesText, "BrushInk", true));
    }

    private static TextBlock MakeStatusText(string text, string brushKey, bool bold)
        => new()
        {
            Text = text,
            FontSize = 13,
            Foreground = Br(brushKey),
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.ExtraBold : Microsoft.UI.Text.FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };

    private static TextBlock MakeSep()
        => new()
        {
            Text = "·",
            FontSize = 13,
            Foreground = Br("BrushMuted"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 14, 0),
        };

    /// <summary>取主题画刷的简写（本文件里到处都在用，省掉重复的强转噪音）。</summary>
    private static Brush Br(string key) => (Brush)Application.Current.Resources[key];

    // ---- 步骤 3 的其它交互 ----

    /// <summary>「重新对比」：回步骤 2 重选范围再比一次。</summary>
    private void OnBackToStep2Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(2);
        SetHint("调整更新范围后可以再对比一次", isError: false);
    }

    private void OnRestoreRecommendClick(object sender, RoutedEventArgs e)
    {
        if (_diff is null)
        {
            return;
        }
        _selected.Clear();
        foreach (var k in ChangeList.RecommendedKeys(_diff.Changes))
        {
            _selected.Add(k);
        }
        RenderChanges();
        RenderStatus();
        SetHint("已恢复推荐勾选", isError: false);
    }

    /// <summary>
    /// 搜索框（Node 的 <c>#search.oninput</c> 有 160ms 防抖）。
    ///
    /// <para>WinUI 没有内置的防抖 TextBox，但列表重绘是同步且很快的
    /// （几百行也就是几十毫秒），这里直接重绘 —— 少一个计时器就少一处
    /// 需要清理的状态。真到列表上千行再说。</para>
    /// </summary>
    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_diff is null)
        {
            return;
        }
        RenderChanges();
    }

    // ------------------------------------------------------------------
    // 进度遮罩（CSS .overlay）
    // ------------------------------------------------------------------

    /// <summary>
    /// 打开遮罩。
    /// </summary>
    /// <param name="indeterminate">
    /// true 时用持续滑动的斜纹条并隐藏百分比。
    /// Node 版在拿不到真实细分进度时也这么做
    /// （<c>overlay.open(..., {indeterminate:true})</c>）——
    /// 「卡在一个假百分比」比「不知道进度」更让人焦虑。
    /// </param>
    private void ShowOverlay(string title, string msg, bool indeterminate)
    {
        OverlayTitle.Text = title;
        OverlayMsg.Text = msg;
        OverlayPct.Text = "0%";
        OverlayBarFill.Width = 0;
        OverlayBarIndeterminate.Visibility = indeterminate ? Visibility.Visible : Visibility.Collapsed;
        OverlayPct.Visibility = indeterminate ? Visibility.Collapsed : Visibility.Visible;

        Overlay.Visibility = Visibility.Visible;

        if (indeterminate && _slideStoryboard is null)
        {
            _slideStoryboard = BuildSlideStoryboard();
        }
        _slideStoryboard?.Begin();
    }

    private void ShowOverlayProgress(string msg, int pct)
    {
        OverlayMsg.Text = msg;
        var clamped = Math.Clamp(pct, 0, 100);

        // 收到真实进度帧 → 退出不确定模式（Node overlay.progress 的
        // `classList.remove('is-loading')`），否则滑块会和百分比打架
        OverlayBarIndeterminate.Visibility = Visibility.Collapsed;
        OverlayPct.Visibility = Visibility.Visible;
        _slideStoryboard?.Stop();

        OverlayPct.Text = ((int)Math.Round((double)clamped)).ToString(CultureInfo.InvariantCulture) + "%";
        // 进度条实际可用宽度 = 440 - 28*2 - 3*2 = 378
        OverlayBarFill.Width = 378 * clamped / 100.0;
    }

    private void HideOverlay()
    {
        _slideStoryboard?.Stop();
        Overlay.Visibility = Visibility.Collapsed;
    }

    private Storyboard? _slideStoryboard;

    /// <summary>
    /// 不确定进度的来回滑动（CSS <c>overlay.is-loading</c> 下进度条里的滑块）。
    ///
    /// <para>WinUI 没有 CSS 动画，但有 Composition 或 Storyboard。这里用
    /// Storyboard 驱动 <c>TranslateTransform.X</c>：从 -100 移到 460 再回来，
    /// 滑块宽 90、轨道可用宽 378，所以两端各留一段空白。</para>
    ///
/// <para><b>不设缓动函数，这是查证后的结论而非省略</b>：
    /// CSS 的 <c>animation</c> 默认就是 linear（<c>linear</c> 是初始值）。
    /// 而 WinUI 3 的 <c>Microsoft.UI.Xaml.Media.Animation</c> 只有
    /// <c>EasingFunctionBase</c> 一族，且<em>全部是幂函数</em>：
    /// QuadraticEase(t²) / CubicEase(t³) / QuarticEase / QuinticEase /
    /// ExponentialEase / CircleEase / SineEase / BackEase / BounceEase /
    /// ElasticEase —— <b>既没有线性，也没有可调控制点的贝塞尔</b>
    /// （查 <c>Microsoft.WinUI.xml</c> 逐个确认过：这些类型只有无参构造，
    /// 没有任何属性可设）。
    /// 既然没有等价物，<c>EasingFunction</c> 留空即默认匀速，
    /// 这恰好就是 CSS 的行为。硬塞一个 t³ 反而会让滑块起步更慢、到顶更快。</para>
    ///
    /// <para><b>EnableDependentAnimation 必须显式关掉</b>：它默认开启，
    /// 会让 Storyboard 每帧重建 Transform 从而掉帧 —— 这正是 M0 里
    /// <c>Translation</c> 动画查了两轮的坑。</para>
    /// </summary>
    private Storyboard BuildSlideStoryboard()
    {
        var sb = new Storyboard { RepeatBehavior = new RepeatBehavior(1) };

        var anim = new DoubleAnimation
        {
            From = -100,
            To = 460,
            Duration = new Duration(TimeSpan.FromMilliseconds(1100)),
            AutoReverse = true,
            EnableDependentAnimation = false,
        };
        Storyboard.SetTarget(anim, OverlaySlide);
        Storyboard.SetTargetProperty(anim, "X");
        sb.Children.Add(anim);
        return sb;
    }

    // ------------------------------------------------------------------
    // 小组件构造
    // ------------------------------------------------------------------

    /// <summary>
    /// 构造一个分类标签（CSS .tag）。
    /// <para>internal 而不是 private：自检要照着它构造目标卡的标签，
    /// 让「样式键写错」这类问题在 <c>--selftest</c> 里就暴露，而不是等运行到那一步。</para>
    /// </summary>
    internal static Border MakeTag(string text, string? tagStyle)
    {
        var border = new Border
        {
            Style = (Style)Application.Current.Resources[tagStyle ?? "PixelTagNeutralStyle"],
            Margin = new Thickness(0, 0, 5, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = (Brush)Application.Current.Resources["BrushInk2"],
            },
        };
        return border;
    }

    /// <summary>
    /// 构造一个更新范围勾选项（CSS .scope + .cb）。
    ///
    /// <para>整体做成 <see cref="ToggleButton"/>：CSS 的 <c>.scope.is-on</c>
    /// 同时改边框色与背景色，WinUI 里这两处由 Checked 视觉状态统一驱动
    /// （见 <c>PixelScopeStyle</c>），比在外面手工切颜色更不容易漏。</para>
    ///
    /// <para><b>勾号为什么不走视觉状态</b>：勾号在 ToggleButton 的
    /// <em>Content</em> 里，而 <c>VisualState</c> 的 <c>Setter Target</c>
    /// 只能在 ControlTemplate 的命名空间里找名字。两者不是同一个树，
    /// 写 <c>Target="CheckMark.Visibility"</c> 会在勾选态生效时抛
    /// 「找不到目标元素」。所以改成在 <c>IsCheckedChanged</c> 里直接改
    /// Visibility —— 同样驱动，但作用域一定对。</para>
    /// </summary>
    internal ToggleButton MakeScopeToggle(ScopeItemView view)
    {
        var root = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        // 勾号（CSS ::after 那笔 L 形）：默认隐藏，勾上时由 IsCheckedChanged 点亮。
        var check = new Border
        {
            Width = 4,
            Height = 9,
            CornerRadius = new CornerRadius(1),
            Background = (Brush)Application.Current.Resources["BrushGrassInk"],
            RenderTransform = new RotateTransform { Angle = 42, CenterX = 2, CenterY = 4.5 },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -2, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        // 17x17 勾选框（CSS .cb）：外框与勾号是**兄弟**节点，不是父子。
        //
        // 【为什么必须是兄弟】Border 只能有一个子元素。把勾号塞进外框的
        // Child 里虽然合法，但那样勾号就成了 Border 的子元素，它上面的
        // HorizontalAlignment/VerticalAlignment 会被 Border 的内容对齐覆盖，
        // 在 Border 里居中的效果依赖 Border 自身的 Padding，
        // 表现就是勾号贴边。用同一个 Grid 下的兄弟节点，对齐语义才明确。
        var box = new Grid
        {
            Width = 17,
            Height = 17,
            Margin = new Thickness(0, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        box.Children.Add(new Border
        {
            Width = 17,
            Height = 17,
            CornerRadius = new CornerRadius(3),
            Background = (Brush)Application.Current.Resources["BrushCard"],
            BorderBrush = (Brush)Application.Current.Resources["BrushEdge"],
            BorderThickness = new Thickness(2.5),
        });
        box.Children.Add(check);

        root.Children.Add(box);

        // 文本块：标题 + 数量 + 说明
        var text = new StackPanel { Spacing = 2 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        titleRow.Children.Add(new TextBlock
        {
            Text = view.Title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["BrushInk"],
        });
        titleRow.Children.Add(new TextBlock
        {
            // CSS: 空时显示「（包里没有）」，否则「N 项」
            Text = view.IsEmpty ? "（包里没有）" : $"{view.Count} 项",
            FontSize = (double)Application.Current.Resources["FontSizeSmall"],
            Foreground = (Brush)Application.Current.Resources["BrushMuted"],
        });
        text.Children.Add(titleRow);
        text.Children.Add(new TextBlock
        {
            Text = view.Desc,
            FontSize = (double)Application.Current.Resources["FontSizeSmall"],
            Foreground = (Brush)Application.Current.Resources["BrushMuted"],
            TextWrapping = TextWrapping.Wrap,
        });
        root.Children.Add(text);

        var toggle = new ToggleButton
        {
            Name = "ScopeToggle",
            Style = (Style)Application.Current.Resources["PixelScopeStyle"],
            Content = root,
            Tag = view.Key,
        };
        // 【为什么用 Checked/Unchecked 而不是 IsCheckedChanged】
        // 后者是 CheckBox 的事件，ToggleButton 上没有（编译期才发现）。
        // Checked/Unchecked 是 ToggleButton 自带的，等价覆盖两种切换方向。
        void SyncCheckMark()
            => check.Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        toggle.Checked += (_, _) => SyncCheckMark();
        toggle.Unchecked += (_, _) => SyncCheckMark();
        toggle.Click += OnScopeToggleClick;
        return toggle;
    }

    private void OnScopeToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string key } toggle && toggle.IsEnabled)
        {
            if (toggle.IsChecked == true)
            {
                _scope.Add(key);
            }
            else
            {
                _scope.Remove(key);
            }
            UpdateDiffButtonState();
        }
    }

    // ------------------------------------------------------------------
    // 提示行
    // ------------------------------------------------------------------

    /// <summary>CSS .hint / .hint.is-error（错误时文字变红加粗）。</summary>
    private void SetHint(string text, bool isError)
    {
        HintText.Text = text;
        HintText.Foreground = isError
            ? (Brush)Application.Current.Resources["BrushRed"]
            : (Brush)Application.Current.Resources["BrushMuted"];
        HintText.FontWeight = isError
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
    }
}
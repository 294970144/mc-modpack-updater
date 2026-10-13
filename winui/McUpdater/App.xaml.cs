using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace McUpdater;

/// <summary>
/// 应用入口。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 置顶窗口（仅 <c>--show</c> 截图模式用）。
    ///
    /// <para><b>为什么不用 AppWindow.Presenter 的 IsAlwaysOnTop</b>：实测设了之后
    /// 桌面上别的置顶窗口（远控悬浮窗）照样压上来 —— Presenter 那个开关只在
    /// 窗口刚创建的那一刻生效，之后被别的置顶窗口激活就排到我们前面了。
    /// Win32 的 SetWindowPos(HWND_TOPMOST) 是把窗口重新插到置顶链的顶部，
    /// 时序上最后说才算数。</para>
    /// </summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);

    /// <summary>主窗口。unpackaged 下用静态字段持有，避免被 GC 回收导致关窗即崩。</summary>
    public static Window? Window { get; private set; }

    /// <summary>主窗口 HWND：unpackaged 下传统 WinRT Picker 需要它（AUMID 只在 Shell 激活时才必需，
    /// 显式给 HWND 后走标准 COM 对话框路径）。</summary>
    public static nint WindowHandle { get; private set; }

    public App()
    {
        InitializeComponent();

        // 注册 GBK 代码页：.NET Core/.NET 5+ 默认不含 936 编码。
        // 不注册则 Encoding.GetEncoding("gbk") 抛 NotSupportedException，
        // PCL2 导出的整合包里中文 mod 文件名会全部乱码（Node 内置 full-icu 无此坑，必然漏）。
        System.Text.Encoding.RegisterProvider(
            System.Text.CodePagesEncodingProvider.Instance);

        UnhandledException += OnUnhandledException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 自检模式：McUpdater.exe --selftest "<整合包路径>"
        // 走完读包与 UI 数据映射并把结果 dump 出来，不建窗口。
        //
        // 【为什么不靠截图验收】WinUI 3 的客户区是 DirectComposition 合成，
        // PrintWindow 抓回来全白（标题栏能抓，因为它还是 GDI 画的）；
        // 屏幕抓取在无头会话里又拍不到窗口。截图只能证明"看起来有东西"，
        // 自检能证明"那东西是什么"。
        var argv = Environment.GetCommandLineArgs();
        var selftestIdx = Array.IndexOf(argv, "--selftest");
        if (selftestIdx >= 0 && selftestIdx + 1 < argv.Length)
        {
            // 自检要构造真实控件（范围勾选项），所以必须先建窗口拿到
            // 资源字典与 DispatcherQueue，但不Activate —— 自检不需要显示。
            var probe = new MainWindow();
            var code = await SelfTest.RunAsync(argv[selftestIdx + 1], probe);

            // --show：自检后把窗口真显示出来（已渲染好步骤 2），供整屏截图。
            // 截图是目前唯一能拿到"界面长什么样"证据的路 —— PrintWindow 抓不到
            // DirectComposition 客户区（M0/M3 都试过），但 gdigrab 整屏抓没问题。
            if (Array.IndexOf(argv, "--show") >= 0)
            {
                try
                {
                    // 固定位置与尺寸：避免窗口落到副屏或被任务栏切掉，
                    // 截图就成了一张可对比的基准图。
                    probe.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(80, 80, 1280, 840));
                    // 置顶：截图时桌面上别的窗口（IM、远控悬浮窗）会压住我们，
                    // 裁出来的图缺一块就白截了。仅 --show 模式置顶，正常运行不受影响。
                    // 【API 备注】AppWindow 上没有 IsAlwaysOnTop 属性（编译期就报 CS1061），
                    // 这个开关在 OverlappedPresenter 上 —— WinAppSDK 把窗口行为拆进了 Presenter。
                    if (probe.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                    {
                        presenter.IsAlwaysOnTop = true;
                    }
                }
                catch
                {
                    // AppWindow API 不可用就用默认位置，不影响验证
                }
                // --step3：把自检算好的变更清单渲染出来（默认渲染步骤 2）
                var step3 = Array.IndexOf(argv, "--step3") >= 0;
                if (step3 && SelfTest.LastPlan is not null)
                {
                    probe.RenderStep3ForSelfTest(SelfTest.LastPlan);
                }

                // --target <dir>：模拟「手动选择」指定一个游戏目录，
                // 用来截"手动指定"态的目标卡（自动匹配态截不到这个标签）
                var targetIdx = Array.IndexOf(argv, "--target");
                if (targetIdx >= 0 && targetIdx + 1 < argv.Length)
                {
                    try
                    {
                        probe.SetTargetForSelfTest(
                            McUpdater.Core.ClientScanner.DescribeDir(argv[targetIdx + 1]));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[--target] {ex.Message}");
                    }
                }

                probe.Activate();

                // 必须在 Activate 之后调用：先把窗口真正挂上视觉树，
                // 再把它插到置顶链顶部，顺序反了会被后激活的窗口盖回去。
                SetWindowPos(
                    WinRT.Interop.WindowNative.GetWindowHandle(probe),
                    new nint(-1),          // HWND_TOPMOST
                    0, 0, 0, 0,
                    0x0001 | 0x0002);      // SWP_NOSIZE | SWP_NOMOVE

                return;   // 不退出，保持窗口开着
            }

            Exit();
            Environment.Exit(code);
            return;
        }

        // 【重要】unpackaged 下不能设 Application.RequestedTheme ——
        // 它需要包身份（AUMID），设置时抛 COMException 0x80131515，
        // 且这个异常发生在 OnLaunched 内，表现为「双击即退、窗口都不出」。
        // （这次是 M0 探针真启动一次才抓到的：dotnet build 全绿也照样崩。）
        //
        // 锁定浅色的正确做法：把 RequestedTheme="Light" 写在视觉树根元素上。
        // MainWindow.xaml 的根 Grid 已这样设置 —— 框架控件会跟着用 Light 主题资源，
        // 与 Application.RequestedTheme 的效果对视觉树内元素等价。
        // 注意 RequestedTheme 的类型在 Application 上是 ApplicationTheme，
        // 在 UIElement 上是 ElementTheme（Light/Dark/Default）。
        Window = new MainWindow();
        WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(Window);
        Window.Activate();
    }

    /// <summary>
    /// 兜底崩溃记录。注意：XAML 资源加载失败（0xc000027b）是 native fast-fail，
    /// 托管处理器拦不住 —— 这里能记下来的都是普通托管异常。
    /// </summary>
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "McUpdater", "logs");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, "unhandled.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\r\n\r\n");
        }
        catch
        {
            // 日志失败不影响流程
        }
        e.Handled = true;
    }
}

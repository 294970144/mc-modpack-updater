using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace McUpdater.Backgrounds;

/// <summary>
/// 像素生态背景层：3 朵云横向漂移 + 6 片树叶缓落。
///
/// ============================================================================
/// 【动画 API 的真实形态 —— 读 Microsoft.WinUI.dll 元数据实测得出】
///
/// 这里踩了三个错才查对，全部是「UWP 写法套到 WinUI 3」的类型错配，
/// 而且每一处都要等编译报错才发现，一个构建轮次 10~30 秒，代价很高：
///
/// 错法 ①：<c>element.StartAnimation("Translation", "Translation.X", from, to,
///        duration, AnimationTimingFunctions.Linear, AnimationFillMode.HoldEnd)</c>
///        —— 这是 **UWP** 的重载。WinUI 3 的
///        <c>UIElement.StartAnimation</c> 实测只有**一个**重载：
///        <c>StartAnimation(ICompositionAnimationBase)</c>。
///        而且 <c>AnimationTimingFunctions</c> / <c>AnimationFillMode</c>
///        这两个类型在 WinAppSDK 2.5.1 的 1011 个程序集里**根本不存在**。
///
/// 错法 ②：以为 <c>anim.KeyFrames.Add(new Vector3KeyFrame{...})</c>
///        —— 没有 <c>KeyFrames</c> 集合，也没有 <c>Vector3KeyFrame</c> 类型。
///        正确是 <c>anim.InsertKeyFrame(float progress, Vector3 value)</c>，
///        progress 是 0~1 的归一化时间，不是 TimeSpan。
///
/// 错法 ③：<c>CompositionTarget</c> 在 <c>Microsoft.UI.Xaml.Media</c>
///        —— 这个猜对了但同时要using Hosting（为了 ElementCompositionPreview）。
///
/// 【正确姿势】
///     var visual = PrepareTranslatable(element);   // 启用 + 预热，见该方法注释
///     var compositor = CompositionTarget.GetCompositorForCurrentThread();
///     var anim = compositor.CreateVector3KeyFrameAnimation();
///     anim.InsertKeyFrame(0f, new Vector3(...));
///     anim.InsertKeyFrame(1f, new Vector3(...));
///     anim.Duration = TimeSpan.FromSeconds(n);
///     anim.IterationBehavior = AnimationIterationBehavior.Forever;
///     visual.StartAnimation("Translation", anim);
/// 动画挂在 **Visual** 上，不是 UIElement 上 —— 这是整件事的关键。
///
/// 【错法 ④：漏了 SetIsTranslationEnabled —— 运行期才炸】
/// 上面那句 <c>StartAnimation("Translation", ...)</c> 如果直接写（本文件初版就是），
/// 运行期会抛：
///     System.ArgumentException: The specified property was not found or
///     cannot be animated. Context: Translation
///
/// 因为 <c>Translation</c> **不在 Visual 的官方可动画属性清单里**。
/// 微软文档列出的 9 个是：Size / Offset / Opacity / Orientation /
/// CenterPoint / RotationAngle / RotationAngleInDegrees / RotationAxis /
/// TransformMatrix —— 确实没有 Translation。
///
/// 但**它并不是被 WinUI 3 移除了**（M0-探针结论.md 第 7 条当时就是这么误判的，
/// 而那个错误结论直接误导了本文件的初版实现）。真相是：Translation 是
/// **默认未启用**的属性，必须先对元素调SetIsTranslationEnabled 才会被创建。
///
/// 【为什么不能用 Offset 代替 Translation】
/// M0 探针量到的「2 秒位移 1064px」其实是 **Offset** 路线测出来的。
/// 之所以能跑：云在 Canvas 里布局几乎不变，Offset 没被布局 pass 覆写 ——
/// 这是**侥幸有效**，不是方案正确。
///
/// Offset 是 XAML 渲染引擎写的绝对位置，布局 pass 会把它整个覆盖
/// （微软官方博客称之为 stomping），并**打断正在跑的动画**。
/// Translation 是渲染期 post-layout 的叠加位移，框架层对它「无感知」，
/// 永不被覆写。文档原句：
///     "Translation always applies as an additive to the current Offset."
///
/// 【为什么以后不再靠猜】
/// 写之前先用 tools/reflect_api（见 M0-探针结论.md 里的做法）读元数据，
/// 或直接查 learn.microsoft.com 的 API 文档确认签名。
/// 猜一次一个构建轮次，反射一次几秒。
///
/// ============================================================================
/// 【为什么用 Composition 而不是 XAML Storyboard —— M0 探针③ 实测】
/// Composition 动画跑在 compositor 线程：M0 实测 3 个云 2 秒位移 1064px，
/// 同时 UI 线程 DispatcherTimer 抖动恒定 33.9ms 不增长。
/// 所以背景几十个元素与后台 diff 的 CRC32 计算可安全共存。
/// XAML Storyboard 跑 UI 线程，多元素时必然与后台计算抢线程。
///
/// 【互斥约束】
/// Composition 的 Translation/Rotation/Scale 与 RenderTransform /
/// RenderTransformOrigin / Projection 互斥，混用会 API 调用失败。
/// 本控件只用 Composition；Controls.xaml 的按钮只用 Layout 属性，互不干扰。
/// </summary>
public sealed partial class PixelBackground : UserControl
{
    /// <summary>树叶落完一轮的时长（秒），各片不同步，避免同时落地。</summary>
    private static readonly double[] LeafFallSeconds =
        { 26, 33, 39, 30, 44, 36 };

    /// <summary>树叶横向摆幅（像素），正负交错形成自然感。</summary>
    private static readonly double[] LeafSway = { 14, -10, 18, -16, 11, -13 };

    public PixelBackground()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ResizeClip();
    }

    private void ResizeClip()
    {
        var w = SceneCanvas.ActualWidth;
        var h = SceneCanvas.ActualHeight;
        if (w > 0 && h > 0)
        {
            SceneClip.Rect = new Windows.Foundation.Rect(0, 0, w, h);
        }
    }

    /// <summary>启动全部动画。必须在窗口 Loaded 之后调用（此时才有实际尺寸）。</summary>
    public void Start()
    {
        ResizeClip();

        // 尊重系统「减少动态效果」（对应 CSS 的 prefers-reduced-motion）
        if (!new UISettings().AnimationsEnabled)
        {
            return;
        }

        // 云朵：起始位置与周期各不相同 → 形成视差，不会整齐同步
        DriftCloud(Cloud1, -200, 18);
        DriftCloud(Cloud2, -180, 26);
        DriftCloud(Cloud3, -160, 34);

        // 树叶：缓落 + 横向摆动
        var leaves = new[] { Leaf1, Leaf2, Leaf3, Leaf4, Leaf5, Leaf6 };
        for (int i = 0; i < leaves.Length; i++)
        {
            FallLeaf(leaves[i], LeafSway[i], LeafFallSeconds[i]);
        }
    }

    /// <summary>
    /// 取出一个<em>可以动画 Translation</em> 的 Composition Visual。
    ///
    /// 【缺任何一步都会踩坑，所以三步绑在一起】
    ///
    /// 第 1 步必须且只能这样开：
    ///     ElementCompositionPreview.SetIsTranslationEnabled(element, true);
    /// 它是<strong>static</strong>方法，没有 Visual 实例可传 —— 只能作用在 UIElement 上。
    /// 必须<strong>直接调在要动画的那个元素上</strong>：调在父元素（SceneCanvas / PixelBackground）
    /// 上对子元素<strong>无效</strong>。漏掉这步 → Visual 上根本不创建 Translation 属性 →
    /// StartAnimation 抛 "The specified property was not found or cannot be animated. Context: Translation"。
    ///
    /// 第 2 步：GetElementVisual 取 handoff visual。它是<strong>异步创建</strong>的
    /// （框架层与 compositor 的交接有延迟），刚拿到的 visual 属性可能还没就绪。
    /// 官方博客给的处方是紧接着预热一次，避免首次使用时序问题。
    ///
    /// 第 3 步不能省，也不能改成 Offset：
    ///     visual.Properties.InsertVector3("Translation", Vector3.Zero);
    /// Offset 会被 XAML 布局 pass 覆盖并打断动画（stomping），详见类型注释。
    /// </summary>
    private static Visual PrepareTranslatable(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        return visual;
    }

    /// <summary>云朵匀速横向漂过窗口，到右缘后自动回到左侧重来一轮。</summary>
    private void DriftCloud(UIElement element, double startX, double seconds)
    {
        var visual = PrepareTranslatable(element);
        var compositor = CompositionTarget.GetCompositorForCurrentThread();

        // 终点要在右缘之外，否则云会有一半悬在窗口外。
        // 注意 Translation 是叠加量：XAML 里Cloud1/2/3 的 Canvas.Left 已归零，
        // 所以这里的 startX / endX 数值 == 元素实际屏幕横坐标，可直接对照读。
        var endX = Math.Max(1200, ElementWidth(element)) + 220;

        var anim = compositor.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(0f, new Vector3((float)startX, 0f, 0f));
        anim.InsertKeyFrame(1f, new Vector3((float)endX, 0f, 0f));

        anim.Duration = TimeSpan.FromSeconds(seconds);
        // Forever 由 compositor 自己循环，不需要 AnimationEnded 回调重启
        // （回调重启每轮都要 Dispose 重建对象，回调抛异常还会静默停止）
        anim.IterationBehavior = AnimationIterationBehavior.Forever;

        visual.StartAnimation("Translation", anim);
    }

    /// <summary>
    /// 树叶下落：四个中间关键帧把横向摆动和垂直下落揉进同一条曲线。
    /// 匀速下落是主方向，摆动叠在上面 —— 视觉上就是 CSS 的树叶缓落。
    /// </summary>
    private void FallLeaf(UIElement element, double swayX, double seconds)
    {
        var visual = PrepareTranslatable(element);
        var compositor = CompositionTarget.GetCompositorForCurrentThread();

        // Y 轴完全由 Translation 接管（XAML 里各 Leaf 的 Canvas.Top 已归零），
        // 所以这两个值就是实际屏幕纵坐标。
        var startY = -60.0;
        var endY = ElementHeight(element) + 80;

        var anim = compositor.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(0f, new Vector3(0f, (float)startY, 0f));

        // 一个来回的摆动：1/4 处到一侧，中点回中，3/4 处到另一侧
        for (int i = 1; i <= 3; i++)
        {
            var progress = i / 4f;
            var y = startY + (endY - startY) * progress;
            var x = (i % 2 == 1) ? swayX : 0;
            anim.InsertKeyFrame(progress, new Vector3((float)x, (float)y, 0f));
        }

        anim.InsertKeyFrame(1f, new Vector3(0f, (float)endY, 0f));

        anim.Duration = TimeSpan.FromSeconds(seconds);
        anim.IterationBehavior = AnimationIterationBehavior.Forever;

        visual.StartAnimation("Translation", anim);
    }

    private static double ElementWidth(UIElement e)
        => e is FrameworkElement fe && fe.ActualWidth > 0 ? fe.ActualWidth : 1200;

    private static double ElementHeight(UIElement e)
        => e is FrameworkElement fe && fe.ActualHeight > 0 ? fe.ActualHeight : 600;
}
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace McUpdater.Controls;

/// <summary>
/// 四步流程步骤条。当前步高亮（草绿序号方块 + 卡片底），已完成的步
/// 用深草绿序号方块，并可点击回退。未到达的步不可点击。
/// </summary>
public sealed partial class StepBar : UserControl
{
    /// <summary>步骤总数。</summary>
    public const int StepCount = 4;

    /// <summary>
    /// 全透明画刷。用资源字典里的 BrushTransparent 而非 Transparent ——
    /// 后者在 WinUI 3 的 C# 投影里不存在（引用报 CS0103），统一走 Palette 的定义，
    /// 也避免项目里出现两处可能不一致的透明色。
    /// </summary>
    private static readonly Brush Transparent =
        (Brush)Application.Current.Resources["BrushTransparent"];

    private readonly Button[] _buttons;
    private readonly Border[] _indexBoxes;
    private readonly TextBlock[] _indexTexts;
    private readonly TextBlock[] _labels;

    private int _current = 1;

    public StepBar()
    {
        InitializeComponent();

        _buttons = new[] { Step1, Step2, Step3, Step4 };
        _indexBoxes = new[] { Step1Box, Step2Box, Step3Box, Step4Box };
        _indexTexts = Array.ConvertAll(_indexBoxes, b => (TextBlock)b.Child);
        _labels = new[] { Step1Label, Step2Label, Step3Label, Step4Label };

        ApplyState();
    }

    /// <summary>用户点击了某一步。参数是 1~4。</summary>
    public event Action<int>? StepClicked;

    /// <summary>
    /// 设置当前步骤（1~4）。传 0 表示当前没有可达步骤（尚未选包）。
    /// </summary>
    public void SetCurrent(int step)
    {
        _current = step;
        ApplyState();
    }

    private void ApplyState()
    {
        for (int i = 0; i < StepCount; i++)
        {
            int n = i + 1;
            var btn = _buttons[i];
            var box = _indexBoxes[i];
            var idxText = _indexTexts[i];
            var label = _labels[i];

            if (n == _current)
            {
                // CSS .step.is-active：卡片底 + 描边 + 硬投影，序号方块草绿
                btn.Background = (Brush)Application.Current.Resources["BrushCard"];
                btn.BorderBrush = (Brush)Application.Current.Resources["BrushLine"];
                box.Background = (Brush)Application.Current.Resources["BrushGrass"];
                box.BorderBrush = (Brush)Application.Current.Resources["BrushEdge"];
                idxText.Foreground = (Brush)Application.Current.Resources["BrushGrassInk"];
                label.Foreground = (Brush)Application.Current.Resources["BrushInk"];
                btn.IsEnabled = true;
            }
            else if (n < _current)
            {
                // CSS .step.is-done：序号方块深草绿，可点击回退
                btn.Background = Transparent;
                btn.BorderBrush = Transparent;
                box.Background = (Brush)Application.Current.Resources["BrushGrassD"];
                box.BorderBrush = (Brush)Application.Current.Resources["BrushEdge"];
                idxText.Foreground = (Brush)Application.Current.Resources["BrushGrassInk"];
                label.Foreground = (Brush)Application.Current.Resources["BrushInk2"];
                btn.IsEnabled = true;
            }
            else
            {
                // 未到达：默认态，禁用（不可点击回退到还没去过的步）
                btn.Background = Transparent;
                btn.BorderBrush = Transparent;
                box.Background = (Brush)Application.Current.Resources["BrushCard3"];
                box.BorderBrush = (Brush)Application.Current.Resources["BrushLine"];
                idxText.Foreground = (Brush)Application.Current.Resources["BrushMuted"];
                label.Foreground = (Brush)Application.Current.Resources["BrushMuted"];
                btn.IsEnabled = false;
            }
        }
    }

    private void OnStepClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } btn
            && int.TryParse(tag, out int step)
            && step != _current)
        {
            StepClicked?.Invoke(step);
        }
    }
}
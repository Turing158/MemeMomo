using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MemeMomo.Models;
using MemeMomo.UI;
using MemeMomo.UI.Animation;
using MemeMomo.UI.Dock;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace MemeMomo.Components;

/// <summary>
/// 便签贴边态的 D 形标签视觉：标题 + 箭头 + 弧端把手。
/// 状态由 <see cref="Edge"/> / <see cref="IsExpanded"/> / <see cref="TabTitle"/> 驱动；
/// 手势（点击、拖动）通过 <see cref="BodyPressed"/> / <see cref="HandlePressed"/> 上抛。
/// <see cref="StripWidth"/> 为有限值时进入"条内模式"：可见标签固定宽度、锚定贴边侧
/// （贴边驻留窗口比标签宽，展开/收起/把手拖动动画此宽度而不改窗口矩形）；
/// NaN 为填充模式：标签铺满宿主窗口（浮动 morph 期间的老行为）。
/// </summary>
public partial class MemoPopoutDockTab : WpfUserControl
{
    public static readonly DependencyProperty EdgeProperty = DependencyProperty.Register(
        nameof(Edge),
        typeof(MainWindowDockEdge),
        typeof(MemoPopoutDockTab),
        new PropertyMetadata(MainWindowDockEdge.Left, OnVisualStateChanged));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded),
        typeof(bool),
        typeof(MemoPopoutDockTab),
        new PropertyMetadata(false, OnVisualStateChanged));

    public static readonly DependencyProperty TabTitleProperty = DependencyProperty.Register(
        nameof(TabTitle),
        typeof(string),
        typeof(MemoPopoutDockTab),
        new PropertyMetadata(string.Empty, OnVisualStateChanged));

    public static readonly DependencyProperty StripWidthProperty = DependencyProperty.Register(
        nameof(StripWidth),
        typeof(double),
        typeof(MemoPopoutDockTab),
        new PropertyMetadata(double.NaN, OnStripWidthChanged));

    public MemoPopoutDockTab()
    {
        InitializeComponent();
        ApplyState(animateArrow: false);
        ApplyStripLayout();
    }

    public MainWindowDockEdge Edge
    {
        get => (MainWindowDockEdge)GetValue(EdgeProperty);
        set => SetValue(EdgeProperty, value);
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public string TabTitle
    {
        get => (string)GetValue(TabTitleProperty);
        set => SetValue(TabTitleProperty, value);
    }

    /// <summary>
    /// 条内模式下可见标签的宽度（DIP）；NaN 表示填充宿主窗口。
    /// 动画此属性即展开/收起：贴边驻留期窗口矩形不变，避免 layered 窗口
    /// 逐帧 resize 造成的呈现滞后（标签震动、贴边闪脱）。
    /// </summary>
    public double StripWidth
    {
        get => (double)GetValue(StripWidthProperty);
        set => SetValue(StripWidthProperty, value);
    }

    /// <summary>tab 主体按下（含单击，手势判定由 controller 完成）。</summary>
    public event MouseButtonEventHandler? BodyPressed;

    /// <summary>弧端把手按下（仅弹出态可达，把手未展开时不响应）。</summary>
    public event MouseButtonEventHandler? HandlePressed;

    // 箭头垂直视觉校正（96 DPI、Microsoft YaHei ‹@17 SemiBold 实测）：行框居中后未旋转 ‹
    // 的墨迹重心偏低约 1，上移 1；旋转 180° 的 › 渲染相位不同、整体偏高 2，下移 2。
    // 两种朝向的墨迹均落在标签垂直中心（行 11–16）且互为镜像；
    // RenderTransform 平移在渲染时按整像素吸附，勿改成分数值。
    private const double UprightArrowShiftY = -1;
    private const double RotatedArrowShiftY = 2;

    private static void OnVisualStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MemoPopoutDockTab)d).ApplyState(animateArrow: true);

    private static void OnStripWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MemoPopoutDockTab)d).ApplyStripLayout();

    /// <summary>
    /// 应用条内/填充布局。条内模式：可见标签固定为 StripWidth × TabHeight、
    /// 水平锚定贴边侧、顶部对齐（贴边驻留时窗口 Top 即标签 Top，预览 morph
    /// 期间标签始终停在落定位置）；填充模式恢复 Stretch 铺满宿主窗口。
    /// </summary>
    private void ApplyStripLayout()
    {
        bool strip = !double.IsNaN(StripWidth);
        double width = strip ? Math.Max(0, StripWidth) : double.NaN;
        double height = strip ? MemoPopoutDockOptions.TabHeight : double.NaN;
        ApplyLayoutBounds(LeftLayout, System.Windows.HorizontalAlignment.Left, width, height);
        ApplyLayoutBounds(RightLayout, System.Windows.HorizontalAlignment.Right, width, height);
    }

    private static void ApplyLayoutBounds(Grid layout, System.Windows.HorizontalAlignment alignment, double width, double height)
    {
        layout.HorizontalAlignment = alignment;
        layout.VerticalAlignment = double.IsNaN(height) ? System.Windows.VerticalAlignment.Stretch : System.Windows.VerticalAlignment.Top;
        layout.Width = width;
        layout.Height = height;
    }

    private void OnBodyPressed(object sender, MouseButtonEventArgs e) => BodyPressed?.Invoke(this, e);

    private void OnHandlePressed(object sender, MouseButtonEventArgs e)
    {
        if (!IsExpanded)
        {
            return;
        }

        HandlePressed?.Invoke(this, e);
        e.Handled = true;
    }

    private void OnHandleMouseEnter(object sender, WpfMouseEventArgs e) => AnimateHandleHighlight(1);

    private void OnHandleMouseLeave(object sender, WpfMouseEventArgs e) => AnimateHandleHighlight(0);

    private void ApplyState(bool animateArrow)
    {
        bool left = Edge != MainWindowDockEdge.Right;
        LeftLayout.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        RightLayout.Visibility = left ? Visibility.Collapsed : Visibility.Visible;
        LeftHandle.IsHitTestVisible = IsExpanded;
        RightHandle.IsHitTestVisible = IsExpanded;

        string fullTitle = MemoPopoutDockOptions.GetTabTitle(TabTitle);
        string collapsedTitle = MemoPopoutDockOptions.GetCollapsedTabTitle(TabTitle);
        LeftTitle.Text = IsExpanded ? fullTitle : collapsedTitle;
        RightTitle.Text = IsExpanded ? fullTitle : collapsedTitle;
        TextTrimming trimming = IsExpanded ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        LeftTitle.TextTrimming = trimming;
        RightTitle.TextTrimming = trimming;
        AutomationProperties.SetName(this, fullTitle);

        // 左边缘：未弹出 ›（指向屏幕内，窗口滑出方向），弹出旋转 180° 成 ‹（收回方向）；
        // 右边缘整体镜像。旋转会上下镜像字形墨迹，垂直校正随朝向切换（见 ArrowShiftY 常量）。
        double baseAngle = left ? 180 : 0;
        double target = baseAngle + (IsExpanded ? 180 : 0);
        double targetShift = NormalizeAngle(target) == 180 ? RotatedArrowShiftY : UprightArrowShiftY;
        ApplyArrowFlip(LeftArrowRotation, LeftArrowShift, target, targetShift, animateArrow);
        ApplyArrowFlip(RightArrowRotation, RightArrowShift, target, targetShift, animateArrow);
    }

    private static void ApplyArrowFlip(
        RotateTransform rotate,
        TranslateTransform shift,
        double targetAngle,
        double targetShift,
        bool animate)
    {
        double fromAngle = rotate.Angle;
        double fromShift = shift.Y;
        if (!animate || !MotionPreferences.AnimationsEnabled
            || (Math.Abs(NormalizeAngle(fromAngle) - NormalizeAngle(targetAngle)) < 0.001
                && Math.Abs(fromShift - targetShift) < 0.001))
        {
            rotate.Angle = targetAngle;
            shift.Y = targetShift;
            return;
        }

        MotionAnimations.Start(
            rotate,
            TimeSpan.FromMilliseconds(190),
            MotionEasing.CubicEaseOut,
            progress =>
            {
                rotate.Angle = fromAngle + ((targetAngle - fromAngle) * progress);
                shift.Y = fromShift + ((targetShift - fromShift) * progress);
            });
    }

    private static double NormalizeAngle(double angle)
    {
        double normalized = angle % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private void AnimateHandleHighlight(double target)
    {
        Border highlight = Edge == MainWindowDockEdge.Right ? RightHandleHighlight : LeftHandleHighlight;
        double from = highlight.Opacity;
        if (!MotionPreferences.AnimationsEnabled || Math.Abs(from - target) < 0.001)
        {
            highlight.Opacity = target;
            return;
        }

        MotionAnimations.Start(
            highlight,
            TimeSpan.FromMilliseconds(120),
            MotionEasing.CubicEaseOut,
            progress => highlight.Opacity = from + ((target - from) * progress));
    }
}

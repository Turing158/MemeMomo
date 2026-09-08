using System.Globalization;
using System.Windows;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.UI.Windows;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace MemeMomo.UI.Dock;

/// <summary>
/// 便签贴边参数与纯函数（贴边/脱离触发判定、贴边目标 rect、PopLength clamp、标题规则）。
/// 触发判定与主窗口（MainWindow.Docking.cs）同口径：贴边按指针到工作区边的距离，
/// 脱离按指针向内离开贴边边的距离。不持有窗口引用，便于单元测试。
/// </summary>
internal static class MemoPopoutDockOptions
{
    /// <summary>贴边 tab 高度（DIP），与窗口落定高度同源，避免两处漂移。</summary>
    public const double TabHeight = MemoWindowPlacement.DockedTabHeightDip;
    /// <summary>对侧圆角半径 = TabHeight / 2，呈半圆弧。</summary>
    public const double TabCornerRadius = TabHeight / 2;
    /// <summary>未弹出宽度：边缘留白 + 单个标题字符 + 箭头 + 弧端，按实际渲染微调。</summary>
    public const double CollapsedWidth = 50;
    /// <summary>
    /// 贴边驻留窗口宽度（DIP）= CollapsedWidth + MaxPopLength，容纳最大弹出宽度。
    /// 贴边期间窗口矩形固定不变（透明余量点击穿透），展开/收起/把手拖动只动画
    /// 标签在条内的可见宽度：layered 窗口逐帧 resize 时呈现位图滞后于 HWND 矩形，
    /// 会导致标签整体震动、贴边瞬间脱开又贴回，因此驻留期禁止改窗口尺寸。
    /// </summary>
    public const double DockStripWidthDip = CollapsedWidth + MaxPopLength;
    public const double DefaultPopLength = PopoutDockSettings.DefaultPopLength;
    public const double MinPopLength = PopoutDockSettings.MinPopLength;
    public const double MaxPopLength = PopoutDockSettings.MaxPopLength;
    /// <summary>拖动中指针到工作区左/右边的贴边判定距离（物理像素），与主窗口 DockDetectionPixels 一致。</summary>
    public const double SnapThresholdPixels = 40;
    /// <summary>贴边态指针向内离开贴边边的脱离距离（物理像素），与主窗口 DockDetachPixels 一致。</summary>
    public const double UndockThresholdPixels = 48;
    /// <summary>贴边态手势启动的位移阈值（DIP，欧氏距离），与主窗口 DockDragThresholdDips 一致；未达到视为单击。</summary>
    public const double DragStartThresholdDips = 5;
    /// <summary>弧端把手命中区宽度（DIP）。</summary>
    public const double HandleHitWidth = 18;
    public const string FallbackTitle = "备忘录";

    public static double ClampPopLength(double value) => PopoutDockSettings.ClampPopLength(value);

    /// <summary>弹出态窗口总宽度（DIP）= CollapsedWidth + PopLength。</summary>
    public static double ComputeExpandedWidth(double popLength) => CollapsedWidth + ClampPopLength(popLength);

    /// <summary>
    /// 贴边条内可见标签的屏幕 rect（DIP）：条窗口锚定贴边侧，标签宽度为
    /// <paramref name="tabWidthDip"/>；宽度不小于条宽（填充模式）时整个条即标签。
    /// </summary>
    public static WpfRect TabBoundsWithinStripDip(MainWindowDockEdge edge, WpfRect stripDip, double tabWidthDip)
    {
        if (!double.IsFinite(tabWidthDip) || tabWidthDip >= stripDip.Width)
        {
            return stripDip;
        }

        double left = edge == MainWindowDockEdge.Right ? stripDip.Right - tabWidthDip : stripDip.Left;
        return new WpfRect(left, stripDip.Top, tabWidthDip, stripDip.Height);
    }

    /// <summary>从窗口总宽度（DIP）反推 PopLength。</summary>
    public static double PopLengthFromWidth(double widthDip) => ClampPopLength(widthDip - CollapsedWidth);

    /// <summary>tab 完整标题；空回退"备忘录"。</summary>
    public static string GetTabTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? MemeMomo.Services.LocalizationService.Get(FallbackTitle) : title.Trim();

    /// <summary>
    /// tab 未弹出标题：完整标题的第一个文本元素（按字素切分，避免截断代理对/emoji）；
    /// 空标题回退显示"备"。
    /// </summary>
    public static string GetCollapsedTabTitle(string? title)
    {
        string full = GetTabTitle(title);
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(full);
        return enumerator.MoveNext() ? (string)enumerator.Current! : FallbackTitle;
    }

    /// <summary>
    /// 由把手水平位移推算新的 PopLength。左边缘向右拖加长，右边缘向左拖加长。
    /// horizontalDeltaPixels 为物理像素。
    /// </summary>
    public static double ComputePopLength(
        double startLength,
        double horizontalDeltaPixels,
        MainWindowDockEdge edge,
        DpiScale2 dpi)
    {
        double directedDelta = edge == MainWindowDockEdge.Right ? -horizontalDeltaPixels : horizontalDeltaPixels;
        return ClampPopLength(startLength + (directedDelta / Math.Max(0.01, dpi.ScaleX)));
    }

    /// <summary>
    /// 拖动中判定贴边：指针到工作区左/右边的距离 ≤ 阈值（物理像素）时返回对应边缘；
    /// 距离相等时优先左边缘。上下边缘不响应（与主窗口 TryFindDockEdge 同口径，仅取左右）。
    /// </summary>
    public static MainWindowDockEdge? DetectSnapEdge(
        WpfPoint pointerPixels,
        WpfRect workAreaPixels,
        double thresholdPixels = SnapThresholdPixels)
    {
        double leftDistance = Math.Abs(pointerPixels.X - workAreaPixels.Left);
        double rightDistance = Math.Abs(workAreaPixels.Right - pointerPixels.X);
        if (leftDistance <= rightDistance)
        {
            return leftDistance <= thresholdPixels ? MainWindowDockEdge.Left : null;
        }

        return rightDistance <= thresholdPixels ? MainWindowDockEdge.Right : null;
    }

    /// <summary>指针向内到指定工作区边缘的距离（物理像素），与主窗口 IsPastDetachThreshold 同口径。</summary>
    public static double PointerInwardDistancePixels(
        WpfPoint pointerPixels,
        WpfRect workAreaPixels,
        MainWindowDockEdge edge) => edge == MainWindowDockEdge.Right
            ? workAreaPixels.Right - pointerPixels.X
            : pointerPixels.X - workAreaPixels.Left;

    /// <summary>
    /// 脱离贴边 morph 的单帧几何（DIP）：尺寸从贴边 rect 连续过渡到目标浮动 rect；
    /// 位置钉在指针（物理像素）上，窗口内锚点从起始锚点（指针在贴边 tab 上的位置，
    /// 物理像素）过渡到还原后标题栏中心 <paramref name="titleAnchorPixels"/>。
    /// progress=0 时与贴边 rect 重合（首帧零跳变），progress=1 时指针钉在标题栏中心
    /// 且尺寸等于目标，与 morph 后的续拖（MoveFloatingWindow + UndockGrabOffsetPixels）无缝衔接。
    /// </summary>
    public static WpfRect UndockMorphFrameDip(
        WpfRect fromDip,
        WpfRect targetDip,
        WpfPoint pointerPixels,
        WpfPoint startAnchorPixels,
        WpfPoint titleAnchorPixels,
        DpiScale2 dpi,
        double progress)
    {
        double p = Math.Clamp(progress, 0, 1);
        WpfPoint anchor = new(
            startAnchorPixels.X + ((titleAnchorPixels.X - startAnchorPixels.X) * p),
            startAnchorPixels.Y + ((titleAnchorPixels.Y - startAnchorPixels.Y) * p));
        return new WpfRect(
            (pointerPixels.X - anchor.X) / Math.Max(0.01, dpi.ScaleX),
            (pointerPixels.Y - anchor.Y) / Math.Max(0.01, dpi.ScaleY),
            fromDip.Width + ((targetDip.Width - fromDip.Width) * p),
            fromDip.Height + ((targetDip.Height - fromDip.Height) * p));
    }

    /// <summary>
    /// 脱离贴边还原浮动窗口时的拖动锚点（物理像素）：指针必须钉在自定义标题栏的中心
    /// （水平 = 浮动宽度一半，垂直 = <see cref="MemoWindowPlacement.DefaultTitleBarHeightDip"/> 的一半）。
    /// 这是强制口径：morph 与续拖都按此偏移定位，保证松手后再次按下必然命中标题栏；
    /// 若按指针在标签上的相对位置还原，指针会落在编辑区，便签变回浮动态后将无法拖动。
    /// </summary>
    public static WpfPoint UndockGrabOffsetPixels(double floatingWidthDip, DpiScale2 dpi) => new(
        (floatingWidthDip / 2) * Math.Max(0.01, dpi.ScaleX),
        (MemoWindowPlacement.DefaultTitleBarHeightDip / 2) * Math.Max(0.01, dpi.ScaleY));

    /// <summary>
    /// 手势是否达到启动阈值（欧氏距离 ≥ DragStartThresholdDips × 缩放），
    /// 与主窗口 HasReachedDockDragThreshold 同口径。
    /// </summary>
    public static bool HasReachedDragThreshold(WpfPoint pressPixels, WpfPoint currentPixels, double scaling)
    {
        double dx = pressPixels.X - currentPixels.X;
        double dy = pressPixels.Y - currentPixels.Y;
        return Math.Sqrt((dx * dx) + (dy * dy)) >= DragStartThresholdDips * Math.Max(0.01, scaling);
    }
}

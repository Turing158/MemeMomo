using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MemeMomo.Components;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.UI.Animation;
using MemeMomo.UI.Windows;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace MemeMomo.UI.Dock;

/// <summary>便签贴边控制器的内部拖动阶段。</summary>
internal enum MemoPopoutDragPhase
{
    None,
    /// <summary>浮动态自定义拖动（替代 DragMove）。</summary>
    FloatingDrag,
    /// <summary>浮动态拖动进入贴边预览（窗口已 morph 成 D 形幽灵）。</summary>
    FloatingPreview,
    /// <summary>贴边态主体手势（滑动 / 脱离 / 单击未决）。</summary>
    DockedGesture,
    /// <summary>脱离贴边的还原 morph（动画结束后无缝转浮动态拖动）。</summary>
    UndockMorph,
    /// <summary>弧端把手调长手势。</summary>
    HandleGesture,
}

/// <summary>
/// 便签贴边（Edge Dock Tab）编排器：持有窗口引用，负责三态流转
/// （Floating / Docked / DockedExpanded）、自定义拖动与贴边预览、
/// 全部 morph 动画与贴边态防激活。D 形视觉由窗口逐像素透明 +
/// 标签层 WPF 自绘抗锯齿圆弧实现（不用 SetWindowRgn，region 为
/// 二值掩码会使弧边出现阶梯锯齿）。
/// 贴边驻留期（脱离前）窗口矩形固定为 <see cref="MemoPopoutDockOptions.DockStripWidthDip"/>
/// 宽的贴边条：layered 窗口逐帧 resize 时呈现位图滞后于 HWND 矩形，会让标签
/// 整体震动、贴边瞬间脱开又贴回，因此展开/收起/把手拖动只动画标签层在条内的
/// 可见宽度（<see cref="MemoPopoutDockTab.StripWidth"/>），不改窗口矩形。
/// 贴边预览把窗口矩形一次 morph 到贴边条 rect（即落定后的驻留矩形）：任何
/// “由小变大”的窗口 resize 都会让逐帧位图滞后把可见元素拉向贴边的对向方向
/// 反复抽搐（矩形越小时越明显），故贴边条不在落定后或预览后半段再扩张。
/// 半透明残影的“朝贴边方向收拢”由便签内容自身的收拢动画承担
/// （<see cref="MemoPopoutWindow.BeginGhostCollapse"/>：内容锚定贴边侧、宽度
/// 从浮动宽度收拢到标签宽度，与矩形 morph 同步），并与 D 形标签交叉淡出。
/// 松手落定时矩形已在贴边条上（未走完则以同目标续完），全程不再有可见的整窗
/// 消失：可见期间任何一次性 resize 都会把上一帧位图映射进新矩形（整块拉伸、
/// 概率性闪出错位残影）。透明度全程用动画过渡：拖拽入边与贴边拖拽淡至半透明
/// （<see cref="PreviewGhostOpacity"/>），松手（落定/退出预览/脱离）淡回不透明。
/// 几何与手势判定逻辑在 <see cref="MemoPopoutDockOptions"/> / <see cref="MemoWindowPlacement"/>。
/// </summary>
internal sealed class MemoPopoutDockController : IDisposable
{
    private static readonly TimeSpan DockMorphDuration = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan PreviewMorphDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan CollapseDuration = TimeSpan.FromMilliseconds(200);

    private const int WmMouseActivate = 0x0021;
    private const nint MaNoActivate = 3;
    private const double PreviewGhostOpacity = 0.75;

    private readonly MemoPopoutWindow _window;
    private readonly MonitorService _monitors;
    private readonly IAnimationFrameSource? _frames;
    private readonly Func<Guid, double>? _loadPopLength;
    private readonly Action<Guid, double>? _savePopLength;

    private MemoPopoutDockMode _mode = MemoPopoutDockMode.Floating;
    private MemoPopoutDragPhase _phase = MemoPopoutDragPhase.None;
    private MainWindowDockEdge _edge = MainWindowDockEdge.Left;
    private WpfRect? _floatingBounds;
    private WpfRect? _previewFloatingBounds;
    private double _popLength = MemoPopoutDockOptions.DefaultPopLength;
    private double _floatingMinWidth;
    private double _floatingMinHeight;

    private WpfPoint _grabOffsetPixels;
    private double _slideGrabOffsetYPixels;
    private WpfPoint _pressPointerPixels;
    private WpfPoint _latestPointerPixels;
    private double _pressPopLength;
    private bool _dockDragStarted;
    private PixelMonitorInfo _dockMonitorInfo;

    private FrameAnimation? _morph;
    private Action<double>? _morphFrame;
    private Action? _morphCompleted;
    private WpfRect _morphFrom;
    private WpfRect _morphTarget;
    private bool _morphPointerAnchored;
    /// <summary>脱离 morph 起始锚点（物理像素）：指针在贴边 tab 上的位置；morph 中缓动到 <see cref="_grabOffsetPixels"/>（标题栏中心）。</summary>
    private WpfPoint? _morphAnchorFrom;

    // Window size changes raised from CompositionTarget.Rendering can re-enter
    // WPF's render/layout pipeline through HwndTarget.OnResize.  Keep the
    // animation geometry and commit the latest rectangle after the render
    // callback has returned, coalescing all intermediate frames.
    private DispatcherOperation? _windowRectCommit;
    private WpfRect _pendingWindowRect;
    private bool _hasPendingWindowRect;
    private Action? _pendingWindowRectCompleted;

    /// <summary>贴边条内标签可见宽度动画（展开/收起）。与窗口矩形 morph 分开管理。</summary>
    private FrameAnimation? _stripAnimation;

    /// <summary>整窗透明度过渡动画（进/退预览、贴边落定、贴边拖拽、脱离共用）。</summary>
    private FrameAnimation? _opacityFade;

    private nint _hwnd;
    private HwndSource? _source;
    private bool _attached;
    private int _disposed;

    public MemoPopoutDockController(
        MemoPopoutWindow window,
        MonitorService monitors,
        IAnimationFrameSource? frames,
        Func<Guid, double>? loadPopLength,
        Action<Guid, double>? savePopLength)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _frames = frames;
        _loadPopLength = loadPopLength;
        _savePopLength = savePopLength;
    }

    public MemoPopoutDockMode Mode => _mode;

    public bool IsEdgeDocked => _mode is MemoPopoutDockMode.Docked or MemoPopoutDockMode.DockedExpanded;

    /// <summary>贴边功能开关（设置中的“便签贴边”）。关闭时拖到屏幕边缘不再触发贴边。</summary>
    public bool EdgeDockEnabled { get; private set; } = true;

    /// <summary>
    /// 应用设置中的贴边开关。关闭时已贴边的便签立即程序化还原为浮动窗口
    /// （与主窗口关闭贴边开关时立即还原展开态的行为一致）；手势进行中无法
    /// 打断则保持现状，用户可手动拖出。
    /// </summary>
    public void SetEdgeDockEnabled(bool enabled)
    {
        if (EdgeDockEnabled == enabled)
        {
            return;
        }

        EdgeDockEnabled = enabled;
        if (!enabled)
        {
            PopOutFromDock();
        }
    }

    /// <summary>挂接事件与原生钩子。须在窗口 Loaded 后调用（模板元素与 HWND 均已就绪）。</summary>
    internal void Attach()
    {
        if (_attached || _disposed != 0)
        {
            return;
        }

        _attached = true;
        _floatingMinWidth = _window.MinWidth;
        _floatingMinHeight = _window.MinHeight;
        _hwnd = new WindowInteropHelper(_window).Handle;
        _source = _hwnd != nint.Zero ? HwndSource.FromHwnd(_hwnd) : null;
        _source?.AddHook(WndProc);
        _window.RootElement.MouseMove += OnWindowMouseMove;
        _window.RootElement.MouseLeftButtonUp += OnWindowMouseLeftButtonUp;
        _window.RootElement.LostMouseCapture += OnWindowLostMouseCapture;
        _window.TabLayer.BodyPressed += OnTabBodyPressed;
        _window.TabLayer.HandlePressed += OnTabHandlePressed;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancelMorph(applyTerminalState: false);
        CancelPendingWindowRect();
        CancelStripAnimation();
        CancelOpacityFade();
        if (_attached)
        {
            _window.RootElement.MouseMove -= OnWindowMouseMove;
            _window.RootElement.MouseLeftButtonUp -= OnWindowMouseLeftButtonUp;
            _window.RootElement.LostMouseCapture -= OnWindowLostMouseCapture;
            _window.TabLayer.BodyPressed -= OnTabBodyPressed;
            _window.TabLayer.HandlePressed -= OnTabHandlePressed;
        }

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        _hwnd = nint.Zero;
    }

    // —— 浮动拖动（标题栏按下） ——

    /// <summary>标题栏左键按下：开始自定义拖动（替代 DragMove，便于实时判定贴边）。返回是否接管了本次按下。</summary>
    public bool OnFloatingDragStarted(MouseButtonEventArgs e)
    {
        if (_disposed != 0
            || _phase != MemoPopoutDragPhase.None
            || _mode != MemoPopoutDockMode.Floating
            || e.ChangedButton != MouseButton.Left
            || e.ButtonState != MouseButtonState.Pressed)
        {
            return false;
        }

        WpfPoint pointer = PointerFromEventArgs(e);
        _phase = MemoPopoutDragPhase.FloatingDrag;
        _latestPointerPixels = pointer;
        WpfRect windowPixels = CurrentWindowRectPixels();
        _grabOffsetPixels = new WpfPoint(pointer.X - windowPixels.Left, pointer.Y - windowPixels.Top);
        if (!Mouse.Capture(_window.RootElement, CaptureMode.Element))
        {
            _phase = MemoPopoutDragPhase.None;
            return false;
        }

        MoveFloatingWindow(pointer);
        return true;
    }

    private void MoveFloatingWindow(WpfPoint pointer)
    {
        CancelPendingWindowRect();
        PixelMonitorInfo monitor = _monitors.FromPoint(pointer);
        double scaleX = Math.Max(0.01, monitor.Dpi.ScaleX);
        double scaleY = Math.Max(0.01, monitor.Dpi.ScaleY);
        _window.Left = (pointer.X - _grabOffsetPixels.X) / scaleX;
        _window.Top = (pointer.Y - _grabOffsetPixels.Y) / scaleY;
    }

    private void TryEnterPreviewFromDrag()
    {
        // 设置关闭贴边时浮动拖动保持普通拖动：不到边缘判定，也不会进入贴边预览。
        if (!EdgeDockEnabled)
        {
            return;
        }

        // 与主窗口一致：按指针到工作区左/右边的距离判定贴边，与窗口位置无关。
        PixelMonitorInfo monitor = _monitors.FromPoint(_latestPointerPixels);
        MainWindowDockEdge? edge = MemoPopoutDockOptions.DetectSnapEdge(_latestPointerPixels, monitor.WorkingArea);
        if (edge is MainWindowDockEdge found)
        {
            EnterPreview(found, monitor);
        }
    }

    private void EnterPreview(MainWindowDockEdge edge, PixelMonitorInfo monitor)
    {
        _edge = edge;
        _dockMonitorInfo = monitor;
        _previewFloatingBounds = CurrentDipBounds();
        _phase = MemoPopoutDragPhase.FloatingPreview;
        EnterDockChrome();
        // 窗口矩形一次 morph 到贴边条 rect（落定后的驻留矩形），落定后矩形不再
        // 变：从标签 rect 向贴边条扩张的 resize 会让逐帧位图滞后把可见标签拉向
        // 贴边对向方向反复抽搐。半透明残影的“朝贴边方向收拢”由便签内容自身的
        // 宽度收拢动画承担：内容锚定贴边侧、宽度从浮动宽度收拢到标签宽度，方向
        // 朝贴边且远快于矩形向内的扩张，并与标签交叉淡化。
        _window.TabLayer.StripWidth = MemoPopoutDockOptions.CollapsedWidth;
        _window.BeginGhostCollapse(anchorRight: edge == MainWindowDockEdge.Right, PreviewMorphDuration);
        _window.ShowTabVisual(showTab: true, PreviewMorphDuration);
        BeginOpacityFade(PreviewGhostOpacity, MotionPreferences.FastDuration);
        UpdateTabState();
        BeginMorph(
            ComputeDockedTarget(MemoPopoutDockOptions.DockStripWidthDip),
            PreviewMorphDuration,
            MotionEasing.CubicEaseOut,
            pointerAnchored: false,
            completed: null);
    }

    private void UpdatePreview(WpfPoint pointer)
    {
        // 与主窗口 DockPreview 分支同口径：指针仍在贴边带内 → 跟随/换边；
        // 指针向内离开贴边边超过脱离阈值 → 退出预览。40-48px 之间为滞回带，保持不动。
        PixelMonitorInfo monitor = _monitors.FromPoint(pointer);
        MainWindowDockEdge? edge = MemoPopoutDockOptions.DetectSnapEdge(pointer, monitor.WorkingArea);
        if (edge is MainWindowDockEdge found)
        {
            _dockMonitorInfo = monitor;
            if (found != _edge)
            {
                // 拖到了另一侧边缘：残影收拢重新锚定到新边，morph 到新边的贴边条 rect。
                _edge = found;
                _window.ReanchorGhostCollapse(anchorRight: found == MainWindowDockEdge.Right, PreviewMorphDuration);
                UpdateTabState();
                BeginMorph(
                    ComputeDockedTarget(MemoPopoutDockOptions.DockStripWidthDip, pointer),
                    PreviewMorphDuration,
                    MotionEasing.CubicEaseOut,
                    pointerAnchored: false,
                    completed: null);
                return;
            }

            // 跟随光标沿边滑动（仅改 Top，morph 帧负责矩形）。
            CancelPendingWindowRect();
            WpfRect target = ComputeDockedTarget(MemoPopoutDockOptions.DockStripWidthDip, pointer);
            _window.Top = target.Top;
            return;
        }

        double inward = MemoPopoutDockOptions.PointerInwardDistancePixels(
            pointer,
            _dockMonitorInfo.WorkingArea,
            _edge);
        if (inward > MemoPopoutDockOptions.UndockThresholdPixels)
        {
            ExitPreview();
        }
    }

    private void ExitPreview()
    {
        WpfRect restore = _previewFloatingBounds ?? CurrentDipBounds();
        _phase = MemoPopoutDragPhase.UndockMorph;
        // 保留浮动拖动的抓取偏移：取消预览 = 无缝继续拖动（抓取点跟随光标）。
        // 透明度动画淡回不透明，与还原 morph 同步进行。
        BeginOpacityFade(1, MotionPreferences.FastDuration);
        _window.ShowTabVisual(showTab: false, MotionPreferences.FastDuration);
        RestoreFloatingChrome();
        BeginMorph(
            MemoWindowPlacement.ClampFloatingBoundsDip(restore, _monitors.FromPoint(_latestPointerPixels)),
            MotionPreferences.FastDuration,
            MotionEasing.CubicEaseOut,
            pointerAnchored: true,
            from: LeaveStripMode(),
            completed: CompleteUndockMorph);
    }

    private void CompleteFloatingDrag()
    {
        MemoPopoutDragPhase phase = _phase;
        _phase = MemoPopoutDragPhase.None;
        ReleasePointerCapture();
        if (phase == MemoPopoutDragPhase.FloatingPreview)
        {
            FinalizeDock();
        }
    }

    // —— 贴边落定 ——

    private void FinalizeDock()
    {
        _floatingBounds = _previewFloatingBounds ?? CurrentDipBounds();
        _mode = MemoPopoutDockMode.Docked;
        _phase = MemoPopoutDragPhase.None;
        _popLength = ResolveSavedPopLength();
        UpdateTabState();
        // 落定不改窗口矩形（预览已 morph 到贴边条，驻留矩形不变）：可见期间任何
        // 一次性 resize 都会把上一帧位图映射进新矩形，尤其是“由小变大”的扩张会
        // 让位图滞后把可见标签拉向贴边对向方向反复抽搐。松手时预览 morph 若尚未
        // 走完，以同目标续完（残影继续朝贴边方向收拢），随后仅用透明度动画淡入，
        // 全程不整窗消失。
        CancelMorph(applyTerminalState: false);
        if (IsCloseRect(CurrentDipBounds(), ComputeDockedTarget(MemoPopoutDockOptions.DockStripWidthDip)))
        {
            BeginOpacityFade(1, MotionPreferences.FastDuration);
        }
        else
        {
            BeginMorph(
                ComputeDockedTarget(MemoPopoutDockOptions.DockStripWidthDip),
                PreviewMorphDuration,
                MotionEasing.CubicEaseOut,
                pointerAnchored: false,
                completed: () =>
                {
                    if (_phase == MemoPopoutDragPhase.None && IsEdgeDocked)
                    {
                        BeginOpacityFade(1, MotionPreferences.FastDuration);
                    }
                });
        }
    }

    private static bool IsCloseRect(WpfRect a, WpfRect b) =>
        Math.Abs(a.Left - b.Left) < 0.5
        && Math.Abs(a.Top - b.Top) < 0.5
        && Math.Abs(a.Width - b.Width) < 0.5
        && Math.Abs(a.Height - b.Height) < 0.5;

    /// <summary>
    /// 整窗透明度动画过渡（CubicEaseOut）：进/退预览、贴边落定、贴边拖拽与脱离
    /// 共用；从当前透明度续变，期间再次调用会平滑接管，终值精确落位。
    /// </summary>
    private void BeginOpacityFade(double targetOpacity, TimeSpan duration)
    {
        CancelOpacityFade();
        double from = _window.Opacity;
        if (Math.Abs(from - targetOpacity) < 0.001)
        {
            _window.Opacity = targetOpacity;
            return;
        }

        FrameAnimation? fade = null;
        fade = new FrameAnimation(_frames);
        _opacityFade = fade;
        fade.Start(
            duration,
            MotionEasing.CubicEaseOut,
            progress =>
            {
                if (ReferenceEquals(_opacityFade, fade))
                {
                    _window.Opacity = from + ((targetOpacity - from) * progress);
                }
            },
            () =>
            {
                if (!ReferenceEquals(_opacityFade, fade))
                {
                    return;
                }

                _opacityFade = null;
                fade.Dispose();
                _window.Opacity = targetOpacity;
            });
    }

    private void CancelOpacityFade()
    {
        _opacityFade?.Cancel();
        _opacityFade?.Dispose();
        _opacityFade = null;
    }

    private double ResolveSavedPopLength()
    {
        if (!_window.IsMemoBound)
        {
            return MemoPopoutDockOptions.DefaultPopLength;
        }

        double saved = _loadPopLength?.Invoke(_window.Memo.Id) ?? MemoPopoutDockOptions.DefaultPopLength;
        return MemoPopoutDockOptions.ClampPopLength(saved);
    }

    // —— 贴边态手势 ——

    private void OnTabBodyPressed(object? sender, MouseButtonEventArgs e)
    {
        if (_disposed != 0
            || _mode == MemoPopoutDockMode.Floating
            || _phase != MemoPopoutDragPhase.None
            || e.ChangedButton != MouseButton.Left
            || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        WpfPoint pointer = PointerFromEventArgs(e);
        CancelStripAnimation();
        // 松手落定的预览 morph 续完可能仍在进行（此时 phase=None）：不截断，让其
        // 自然走完到贴边条；剩余 morph 只写矩形，与手势的沿边 Top 跟随短暂重叠，
        // 与预览期同类呈现。
        _phase = MemoPopoutDragPhase.DockedGesture;
        _dockDragStarted = false;
        _pressPointerPixels = pointer;
        _latestPointerPixels = pointer;
        _slideGrabOffsetYPixels = pointer.Y - CurrentWindowRectPixels().Top;
        _dockMonitorInfo = _monitors.FromPoint(pointer);
        if (!Mouse.Capture(_window.RootElement, CaptureMode.Element))
        {
            _phase = MemoPopoutDragPhase.None;
            return;
        }

        e.Handled = true;
    }

    private void OnTabHandlePressed(object? sender, MouseButtonEventArgs e)
    {
        if (_disposed != 0
            || _mode != MemoPopoutDockMode.DockedExpanded
            || _phase != MemoPopoutDragPhase.None
            || e.ChangedButton != MouseButton.Left
            || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        WpfPoint pointer = PointerFromEventArgs(e);
        CancelStripAnimation();
        _phase = MemoPopoutDragPhase.HandleGesture;
        _pressPopLength = _popLength;
        _pressPointerPixels = pointer;
        _latestPointerPixels = pointer;
        if (!Mouse.Capture(_window.RootElement, CaptureMode.Element))
        {
            _phase = MemoPopoutDragPhase.None;
            return;
        }

        e.Handled = true;
    }

    private void ProcessDockedGestureMove(WpfPoint pointer)
    {
        // 与主窗口贴边拖动同口径：先过 5dip 拖动启动阈值（未达到视为单击），
        // 之后每帧判定——指针仍在贴边带内 → 沿边跟随；向内离开贴边边超过脱离阈值 → 还原。
        if (!_dockDragStarted)
        {
            if (!MemoPopoutDockOptions.HasReachedDragThreshold(
                    _pressPointerPixels,
                    pointer,
                    _dockMonitorInfo.Dpi.ScaleX))
            {
                return;
            }

            _dockDragStarted = true;
            // 贴边拖拽态：整窗淡至半透明，松手（CompleteDockedGesture）淡回不透明。
            BeginOpacityFade(PreviewGhostOpacity, MotionPreferences.FastDuration);
        }

        PixelMonitorInfo monitor = _monitors.FromPoint(pointer);
        MainWindowDockEdge? edge = MemoPopoutDockOptions.DetectSnapEdge(pointer, monitor.WorkingArea);
        if (edge is not null)
        {
            _dockMonitorInfo = monitor;
        }
        else if (MemoPopoutDockOptions.PointerInwardDistancePixels(pointer, _dockMonitorInfo.WorkingArea, _edge)
            > MemoPopoutDockOptions.UndockThresholdPixels)
        {
            BeginUndock();
            return;
        }

        SlideAlongEdge(pointer);
    }

    private void SlideAlongEdge(WpfPoint pointer)
    {
        CancelPendingWindowRect();
        PixelMonitorInfo monitor = MonitorForWindow();
        double scaleY = Math.Max(0.01, monitor.Dpi.ScaleY);
        double heightPixels = Math.Max(1, _window.ActualHeight * scaleY);
        double topPixels = MemoWindowPlacement.ClampWindowTopPixels(
            monitor,
            pointer.Y - _slideGrabOffsetYPixels,
            heightPixels);
        _window.Top = topPixels / scaleY;
    }

    private void ProcessHandleGestureMove(WpfPoint pointer)
    {
        double dx = pointer.X - _pressPointerPixels.X;
        _popLength = MemoPopoutDockOptions.ComputePopLength(
            _pressPopLength,
            dx,
            _edge,
            _monitors.FromPoint(pointer).Dpi);
        // 把手调长只动画标签可见宽度，窗口矩形保持贴边条不变。
        _window.TabLayer.StripWidth = MemoPopoutDockOptions.ComputeExpandedWidth(_popLength);
    }

    private void CompleteDockedGesture()
    {
        // 先落相位再释放捕获：LostMouseCapture 同步触发，避免重入二次结算。
        bool wasTap = !_dockDragStarted;
        _phase = MemoPopoutDragPhase.None;
        _dockDragStarted = false;
        ReleasePointerCapture();
        if (wasTap)
        {
            ToggleExpanded();
        }
        else
        {
            // 拖拽结束：整窗淡回不透明。
            BeginOpacityFade(1, MotionPreferences.FastDuration);
        }
    }

    private void CompleteHandleGesture()
    {
        _phase = MemoPopoutDragPhase.None;
        ReleasePointerCapture();
        if (_window.IsMemoBound)
        {
            _savePopLength?.Invoke(_window.Memo.Id, _popLength);
        }
    }

    private void ToggleExpanded()
    {
        bool expand = _mode == MemoPopoutDockMode.Docked;
        _mode = expand ? MemoPopoutDockMode.DockedExpanded : MemoPopoutDockMode.Docked;
        if (expand)
        {
            _popLength = ResolveSavedPopLength();
        }

        double width = expand
            ? MemoPopoutDockOptions.ComputeExpandedWidth(_popLength)
            : MemoPopoutDockOptions.CollapsedWidth;
        UpdateTabState();
        // 展开/收起只动画标签在贴边条内的可见宽度：窗口矩形不变，
        // 杜绝 layered 窗口逐帧 resize 的呈现滞后（整体震动、贴边闪脱）。
        BeginStripAnimation(
            width,
            expand ? ExpandDuration : CollapseDuration,
            expand ? MotionEasing.CubicEaseOut : MotionEasing.CubicEaseInOut);
    }

    /// <summary>动画贴边条内标签的可见宽度（DIP）。从当前宽度续动，不触碰窗口矩形。</summary>
    private void BeginStripAnimation(double targetWidthDip, TimeSpan duration, MotionEasing easing)
    {
        CancelStripAnimation();
        double from = _window.TabLayer.StripWidth;
        if (double.IsNaN(from))
        {
            return;
        }

        FrameAnimation? animation = null;
        animation = new FrameAnimation(_frames);
        _stripAnimation = animation;
        animation.Start(
            duration,
            easing,
            progress =>
            {
                if (ReferenceEquals(_stripAnimation, animation))
                {
                    _window.TabLayer.StripWidth = Lerp(from, targetWidthDip, progress);
                }
            },
            () =>
            {
                if (!ReferenceEquals(_stripAnimation, animation))
                {
                    return;
                }

                _stripAnimation = null;
                animation.Dispose();
            });
    }

    private void CancelStripAnimation()
    {
        _stripAnimation?.Cancel();
        _stripAnimation?.Dispose();
        _stripAnimation = null;
    }

    // —— 脱离贴边 ——

    private void BeginUndock()
    {
        WpfRect floatingSnapshot = _floatingBounds ?? DefaultFloatingBounds();
        _mode = MemoPopoutDockMode.Floating;
        _phase = MemoPopoutDragPhase.UndockMorph;
        WpfRect target = MemoWindowPlacement.ClampFloatingBoundsDip(
            floatingSnapshot,
            _monitors.FromPoint(_latestPointerPixels));
        // 贴边拖拽中转脱离：透明度动画淡回不透明，与还原 morph 同步进行。
        BeginOpacityFade(1, MotionPreferences.FastDuration);
        _window.ShowTabVisual(showTab: false, MotionPreferences.FastDuration);
        RestoreFloatingChrome();
        // 从可见标签 rect 开始 morph（而非贴边条 rect），首帧与标签像素重合；
        // 标签切回填充模式后随 morph 矩形一起长大，衔接老脱离动画。
        WpfRect tabBounds = LeaveStripMode();
        PrepareUndockAnchor(target);
        BeginMorph(
            target,
            MotionPreferences.FastDuration,
            MotionEasing.CubicEaseOut,
            pointerAnchored: true,
            from: tabBounds,
            completed: CompleteUndockMorph);
    }

    /// <summary>脱离 morph 自然完成：恢复浮动尺寸约束，并按住键状态无缝接回手动拖动。</summary>
    private void CompleteUndockMorph()
    {
        RestoreFloatingSizeConstraints();
        ResumeFloatingDragAfterUndock();
    }
    /// <summary>脱离贴边：拖动锚点固定取还原后浮动窗口标题栏的中心（强制口径），保证 morph 结束后指针始终落在可拖动的标题栏上。</summary>
    private void PrepareUndockAnchor(WpfRect target)
    {
        PixelMonitorInfo monitor = _monitors.FromPoint(_latestPointerPixels);
        _grabOffsetPixels = MemoPopoutDockOptions.UndockGrabOffsetPixels(target.Width, monitor.Dpi);
    }

    private void ResumeFloatingDragAfterUndock()
    {
        if (_phase != MemoPopoutDragPhase.UndockMorph)
        {
            return;
        }

        // 脱离 morph 完成回调（Render 优先级）可能先于排队的松手事件执行：
        // 以物理按键为准，松手后不得把窗口瞬移到指针位置接回拖动。
        bool pressed = Mouse.LeftButton == MouseButtonState.Pressed;
        if (_monitors.TryGetCursorState(out _, out bool physicalPressed))
        {
            pressed = physicalPressed;
        }

        if (pressed)
        {
            _phase = MemoPopoutDragPhase.FloatingDrag;
            MoveFloatingWindow(_latestPointerPixels);
        }
        else
        {
            CompleteFloatingDrag();
        }
    }

    private WpfRect DefaultFloatingBounds() =>
        new(_window.Left, _window.Top, MemoWindowPlacement.DefaultWidthDip, MemoWindowPlacement.DefaultHeightDip);

    // —— 程序化弹出 ——

    /// <summary>
    /// 程序化退出贴边（点击系统通知等无指针手势的外部入口）：还原贴边前的浮动
    /// rect，播放与手动脱离相同的还原 morph；morph 不锚定指针，完成后恢复浮动
    /// 尺寸约束即结束，不接回拖动。浮动态、手势进行中或已释放时返回 false。
    /// </summary>
    internal bool PopOutFromDock()
    {
        if (_disposed != 0
            || !IsEdgeDocked
            || _phase != MemoPopoutDragPhase.None)
        {
            return false;
        }

        WpfRect floatingSnapshot = _floatingBounds ?? DefaultFloatingBounds();
        _mode = MemoPopoutDockMode.Floating;
        _phase = MemoPopoutDragPhase.UndockMorph;
        // 无指针可锚定：按窗口当前所在显示器重 clamp 快照，换屏/改布局后仍落在工作区内。
        WpfRect target = MemoWindowPlacement.ClampFloatingBoundsDip(
            floatingSnapshot,
            MonitorForWindow());
        BeginOpacityFade(1, MotionPreferences.FastDuration);
        _window.ShowTabVisual(showTab: false, MotionPreferences.FastDuration);
        RestoreFloatingChrome();
        WpfRect tabBounds = LeaveStripMode();
        BeginMorph(
            target,
            MotionPreferences.FastDuration,
            MotionEasing.CubicEaseOut,
            pointerAnchored: false,
            from: tabBounds,
            completed: () =>
            {
                _phase = MemoPopoutDragPhase.None;
                RestoreFloatingSizeConstraints();
            });
        return true;
    }

    // —— 窗口 chrome ——

    private void EnterDockChrome()
    {
        _window.MinWidth = 0;
        _window.MinHeight = 0;
        _window.ResizeMode = ResizeMode.NoResize;
        _window.SetDockChromeActive(true);
    }

    private void RestoreFloatingChrome()
    {
        _window.SetDockChromeActive(false);
    }

    /// <summary>
    /// 恢复浮动尺寸约束。必须在 morph 结束后调用：贴边窗口 Height=36，
    /// 若在 morph 期间恢复 MinHeight=180，WPF 会把动画中间帧全部 clamp 到 180。
    /// </summary>
    private void RestoreFloatingSizeConstraints()
    {
        _window.MinWidth = _floatingMinWidth;
        _window.MinHeight = _floatingMinHeight;
        _window.ResizeMode = ResizeMode.CanResize;
    }

    // —— morph 动画 ——

    private void BeginMorph(
        WpfRect target,
        TimeSpan duration,
        MotionEasing easing,
        bool pointerAnchored,
        Action? completed,
        WpfRect? from = null)
    {
        WpfRect current = CurrentDipBounds();
        CancelMorph(applyTerminalState: false);
        CancelStripAnimation();
        _morphFrom = from ?? current;
        _morphTarget = target;
        _morphPointerAnchored = pointerAnchored;
        _morphAnchorFrom = pointerAnchored ? UndockStartAnchorPixels() : null;
        _morphFrame = ApplyMorphFrame;
        _morphCompleted = completed;
        FrameAnimation? animation = null;
        animation = new FrameAnimation(_frames);
        _morph = animation;
        animation.Start(
            duration,
            easing,
            progress => _morphFrame?.Invoke(progress),
            () =>
            {
                if (!ReferenceEquals(_morph, animation))
                {
                    return;
                }

                _morph = null;
                _morphFrame = null;
                Action? finished = _morphCompleted;
                _morphCompleted = null;
                animation.Dispose();
                QueueWindowRectCompletion(finished);
            });
    }

    private void CancelMorph(bool applyTerminalState)
    {
        _pendingWindowRectCompleted = null;
        _morph?.Cancel();
        _morph?.Dispose();
        _morph = null;
        Action<double>? frame = _morphFrame;
        Action? completed = _morphCompleted;
        _morphFrame = null;
        _morphCompleted = null;
        if (applyTerminalState)
        {
            frame?.Invoke(1);
            QueueWindowRectCompletion(completed);
        }
    }

    private void ApplyMorphFrame(double progress)
    {
        double left = Lerp(_morphFrom.Left, _morphTarget.Left, progress);
        double top = Lerp(_morphFrom.Top, _morphTarget.Top, progress);
        double width = Lerp(_morphFrom.Width, _morphTarget.Width, progress);
        double height = Lerp(_morphFrom.Height, _morphTarget.Height, progress);
        if (_morphPointerAnchored)
        {
            // 脱离动画：尺寸从贴边 tab 连续长大到浮动尺寸，窗口始终钉在指针上，
            // 锚点从指针在 tab 上的位置滑向还原后标题栏中心（强制口径），
            // 首帧与贴边 rect 重合，末帧与续拖 MoveFloatingWindow 无缝衔接。
            PixelMonitorInfo monitor = _monitors.FromPoint(_latestPointerPixels);
            WpfRect frame = MemoPopoutDockOptions.UndockMorphFrameDip(
                _morphFrom,
                _morphTarget,
                _latestPointerPixels,
                _morphAnchorFrom ?? _grabOffsetPixels,
                _grabOffsetPixels,
                monitor.Dpi,
                progress);
            left = frame.Left;
            top = frame.Top;
            width = frame.Width;
            height = frame.Height;
        }

        QueueWindowRect(new WpfRect(left, top, width, height));
    }

    private void QueueWindowRect(WpfRect rect)
    {
        if (_disposed != 0 || !IsFiniteRect(rect))
        {
            return;
        }

        _pendingWindowRect = rect;
        _hasPendingWindowRect = true;
        if (_windowRectCommit is { Status: DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing })
        {
            return;
        }

        _windowRectCommit = _window.Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(CommitPendingWindowRect));
    }

    private void CommitPendingWindowRect()
    {
        _windowRectCommit = null;
        if (_disposed != 0)
        {
            return;
        }

        if (_hasPendingWindowRect)
        {
            WpfRect rect = _pendingWindowRect;
            _hasPendingWindowRect = false;
            // All four assignments happen outside CompositionTarget.Rendering.
            // WPF may still perform its normal resize/layout work, but it cannot
            // re-enter the animation callback that requested this frame.
            if (!AreClose(_window.Left, rect.Left))
            {
                _window.Left = rect.Left;
            }
            if (!AreClose(_window.Top, rect.Top))
            {
                _window.Top = rect.Top;
            }
            if (!AreClose(_window.Width, rect.Width))
            {
                _window.Width = rect.Width;
            }
            if (!AreClose(_window.Height, rect.Height))
            {
                _window.Height = rect.Height;
            }
        }

        Action? completed = _pendingWindowRectCompleted;
        _pendingWindowRectCompleted = null;
        completed?.Invoke();

        // A property setter can synchronously cause another animation callback
        // to enqueue a newer frame.  Drain that frame on the next idle turn.
        if ((_hasPendingWindowRect || _pendingWindowRectCompleted is not null) &&
            _windowRectCommit is null &&
            _disposed == 0)
        {
            _windowRectCommit = _window.Dispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                new Action(CommitPendingWindowRect));
        }
    }

    private void QueueWindowRectCompletion(Action? completed)
    {
        if (_disposed != 0 || completed is null)
        {
            return;
        }

        _pendingWindowRectCompleted = completed;
        if (_windowRectCommit is not { Status: DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing })
        {
            _windowRectCommit = _window.Dispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                new Action(CommitPendingWindowRect));
        }
    }

    private void CancelPendingWindowRect()
    {
        _hasPendingWindowRect = false;
        _pendingWindowRectCompleted = null;
        if (_windowRectCommit is not null)
        {
            _windowRectCommit.Abort();
            _windowRectCommit = null;
        }
    }

    private static bool IsFiniteRect(WpfRect rect) =>
        double.IsFinite(rect.Left) && double.IsFinite(rect.Top) &&
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height) &&
        rect.Width >= 0 && rect.Height >= 0;

    private static bool AreClose(double left, double right) => Math.Abs(left - right) < 0.01;

    /// <summary>
    /// 脱离 morph 的起始锚点（物理像素）：使 progress=0 时窗口位置恰好等于当前 rect
    /// （首帧零跳变）。与 ApplyMorphFrame 同用指针所在显示器的 DPI。
    /// </summary>
    private WpfPoint UndockStartAnchorPixels()
    {
        DpiScale2 dpi = _monitors.FromPoint(_latestPointerPixels).Dpi;
        return new WpfPoint(
            _latestPointerPixels.X - (_morphFrom.Left * Math.Max(0.01, dpi.ScaleX)),
            _latestPointerPixels.Y - (_morphFrom.Top * Math.Max(0.01, dpi.ScaleY)));
    }

    // —— 事件分发 ——

    private void OnWindowMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_phase is not (MemoPopoutDragPhase.FloatingDrag
            or MemoPopoutDragPhase.FloatingPreview
            or MemoPopoutDragPhase.DockedGesture
            or MemoPopoutDragPhase.UndockMorph
            or MemoPopoutDragPhase.HandleGesture))
        {
            return;
        }

        // 预览/脱离 morph 期间输入会积压：松手后的 move 仍带着按下状态排队到达，
        // 而实时读取的指针位置已是松手后的新位置；以物理按键判定松手，
        // 否则会把这次移动当作拖动而把窗口瞬间拉出贴边。
        bool physicalKnown = _monitors.TryGetCursorState(out WpfPoint pointer, out bool physicalPressed);
        if (!DockPointerGate.IsDragPressed(
                Mouse.LeftButton == MouseButtonState.Pressed,
                physicalKnown,
                physicalPressed))
        {
            FinishPhaseByRelease();
            return;
        }

        if (!physicalKnown)
        {
            pointer = PointerFromEventArgs(e);
        }

        _latestPointerPixels = pointer;
        switch (_phase)
        {
            case MemoPopoutDragPhase.FloatingDrag:
                MoveFloatingWindow(pointer);
                TryEnterPreviewFromDrag();
                break;
            case MemoPopoutDragPhase.FloatingPreview:
                UpdatePreview(pointer);
                break;
            case MemoPopoutDragPhase.DockedGesture:
                ProcessDockedGestureMove(pointer);
                break;
            case MemoPopoutDragPhase.HandleGesture:
                ProcessHandleGestureMove(pointer);
                break;
        }
    }

    private void OnWindowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _phase == MemoPopoutDragPhase.None)
        {
            return;
        }

        FinishPhaseByRelease();
        e.Handled = true;
    }

    private void FinishPhaseByRelease()
    {
        switch (_phase)
        {
            case MemoPopoutDragPhase.FloatingDrag:
            case MemoPopoutDragPhase.FloatingPreview:
                CompleteFloatingDrag();
                break;
            case MemoPopoutDragPhase.DockedGesture:
                CompleteDockedGesture();
                break;
            case MemoPopoutDragPhase.HandleGesture:
                CompleteHandleGesture();
                break;
            case MemoPopoutDragPhase.UndockMorph:
                // 先落相位：CancelMorph 的 completed 回调（接回拖动）在此路径必须空转。
                _phase = MemoPopoutDragPhase.None;
                ReleasePointerCapture();
                CancelMorph(applyTerminalState: true);
                RestoreFloatingSizeConstraints();
                break;
        }
    }

    private void OnWindowLostMouseCapture(object sender, WpfMouseEventArgs e)
    {
        if (_phase == MemoPopoutDragPhase.None || Mouse.Captured is not null)
        {
            return;
        }

        FinishPhaseByRelease();
    }

    // —— 几何 / 状态辅助 ——

    private void UpdateTabState()
    {
        MemoPopoutDockTab tab = _window.TabLayer;
        tab.Edge = _edge;
        tab.IsExpanded = _mode == MemoPopoutDockMode.DockedExpanded;
    }

    private WpfRect ComputeDockedTarget(double widthDip, WpfPoint? pointer = null)
    {
        WpfPoint reference = pointer ?? _latestPointerPixels;
        PixelMonitorInfo monitor = _monitors.FromPoint(reference);
        double topDip = pointer.HasValue
            ? (reference.Y - _grabOffsetPixels.Y) / Math.Max(0.01, monitor.Dpi.ScaleY)
            : _window.Top;
        return MemoWindowPlacement.DockedTabBoundsDip(_edge, monitor, topDip, widthDip);
    }

    private WpfRect CurrentDipBounds()
    {
        if (_hasPendingWindowRect)
        {
            return _pendingWindowRect;
        }

        return new WpfRect(_window.Left, _window.Top, _window.ActualWidth, _window.ActualHeight);
    }

    /// <summary>
    /// 退出条内模式（标签恢复铺满窗口），返回退出前可见标签的屏幕 rect（DIP），
    /// 供脱离 morph 作为起始矩形（首帧与标签像素重合）。
    /// </summary>
    private WpfRect LeaveStripMode()
    {
        CancelStripAnimation();
        WpfRect tabBounds = MemoPopoutDockOptions.TabBoundsWithinStripDip(
            _edge,
            CurrentDipBounds(),
            _window.TabLayer.StripWidth);
        _window.TabLayer.StripWidth = double.NaN;
        return tabBounds;
    }

    private WpfRect CurrentWindowRectPixels()
    {
        DpiScale2 dpi = DpiCoordinateModel.FromVisual(_window);
        return new WpfRect(
            _window.Left * Math.Max(0.01, dpi.ScaleX),
            _window.Top * Math.Max(0.01, dpi.ScaleY),
            _window.ActualWidth * Math.Max(0.01, dpi.ScaleX),
            _window.ActualHeight * Math.Max(0.01, dpi.ScaleY));
    }

    private PixelMonitorInfo MonitorForWindow() =>
        _monitors.FromPoint(CenterOf(CurrentWindowRectPixels()));

    private static WpfPoint CenterOf(WpfRect rect) =>
        new(rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2));

    private WpfPoint PointerFromEventArgs(WpfMouseEventArgs e)
    {
        if (_monitors.TryGetCursorState(out WpfPoint pointer, out _))
        {
            return pointer;
        }

        return _window.PointToScreen(e.GetPosition(_window));
    }

    private void ReleasePointerCapture()
    {
        if (Mouse.Captured == _window.RootElement)
        {
            _window.RootElement.ReleaseMouseCapture();
        }
    }

    private static double Lerp(double from, double to, double progress) => from + ((to - from) * progress);

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmMouseActivate && IsEdgeDocked)
        {
            handled = true;
            return MaNoActivate;
        }

        return nint.Zero;
    }
}

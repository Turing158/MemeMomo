using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using MemeMomo.Infrastructure;
using MemeMomo.Models;
using MemeMomo.UI;
using MemeMomo.UI.Animation;
using MemeMomo.UI.Windows;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfButton = System.Windows.Controls.Button;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace MemeMomo;

internal readonly record struct DockRuntimeSnapshot(
    DockState State,
    MainWindowDockEdge Edge,
    DockCorner Corner,
    double NormalizedPosition,
    double DockSize,
    WpfRect DockWorkAreaPixels,
    WpfRect MonitorBoundsPixels,
    WpfRect CurrentWorkingAreaPixels,
    DpiScale2 Dpi,
    WpfRect WpfBoundsDip,
    WpfRect PhysicalBoundsPixels,
    ResizeMode ResizeMode,
    bool IsVisible,
    bool Topmost,
    bool DockEnabled,
    bool PersistedDocked,
    DockHandleRenderBackend RenderBackend);

public partial class MainWindow : IDockHandleInputSink
{
    private const int DockDetectionPixels = 40;
    private const int DockDetachPixels = 48;
    private const double DockDragThresholdDips = 5;
    private const double DockIdleScale = 0.9;
    private const double DockActiveScale = 1;
    private const double ExpandedMinWidth = 360;
    private const double ExpandedMinHeight = 480;
    private const double ExpandedTitleBarHeight = 49;

    private readonly DockStateMachine _dockStateMachine = new();
    private readonly MonitorService _dockMonitorService = new();
    private DockWindowAdapter? _dockWindowAdapter;
    private DockHandleWindowController? _dockHandleController;
    private FrameAnimation? _dockAnimation;
    private FrameAnimation? _dockScaleAnimation;
    private Action? _restoreCompletion;
    private DispatcherTimer? _expandedBoundsSaveTimer;
    private DispatcherTimer? _pointerReleaseTimer;
    private Func<AppSettings, Task>? _persistWindowStateAsync;
    private DockVisualFrame _currentDockVisualFrame = DockVisualLayoutCalculator.ForEdge(
        MainWindowDockEdge.Left,
        AppSettings.DefaultMainWindowDockSize);
    private DockVisualFrame _animationFromDockVisualFrame = DockVisualLayoutCalculator.ForEdge(
        MainWindowDockEdge.Left,
        AppSettings.DefaultMainWindowDockSize);
    private DockVisualFrame _animationTargetDockVisualFrame = DockVisualLayoutCalculator.ForEdge(
        MainWindowDockEdge.Left,
        AppSettings.DefaultMainWindowDockSize);
    private WpfPoint _animationFromPosition;
    private double _animationFromWidth;
    private double _animationFromHeight;
    private double _animationFromExpandedOpacity;
    private double _animationFromDockOpacity;
    private WpfPoint _animationTargetPosition;
    private double _animationTargetWidth;
    private double _animationTargetHeight;
    private double _animationTargetExpandedOpacity;
    private double _animationTargetDockOpacity;
    private double _dockVisualOpacity;
    private double _animationFromSurfaceScaleX = 1;
    private double _animationFromSurfaceScaleY = 1;
    private double _animationTargetSurfaceScaleX = 1;
    private double _animationTargetSurfaceScaleY = 1;
    private bool _finalizeDockWhenAnimationCompletes;
    private bool _dockingInitialized;
    private bool _expandedStartupSyncPending;
    private bool _isSynchronizingExpandedStartupBounds;
    private bool _titleDragging;
    private bool _dockDragging;
    private bool _dockDragStarted;
    private bool _dockRightButtonPending;
    private bool _useTitleBarCenterDragAnchor;
    private WpfPoint _pointerGrabOffset;
    private WpfPoint _dockPressScreen;
    private WpfPoint _latestPointerScreen;
    private double _titleBarDragCenterY = ExpandedTitleBarHeight / 2;
    private double _titleBarDragCenterOffsetX;
    private bool _hasMeasuredTitleBarDragCenter;
    private WpfPoint _expandedPosition;
    private double _expandedWidth = 420;
    private double _expandedHeight = 680;
    private bool _hasExpandedBounds;
    private MainWindowDockEdge _dockEdge = MainWindowDockEdge.Left;
    private DockCorner _dockCorner = DockCorner.None;
    private WpfRect _dockWorkArea;
    private DpiScale2 _dockDpi = DpiScale2.Default;
    private double _dockNormalizedPosition = 0.5;
    private double _dockSize = AppSettings.DefaultMainWindowDockSize;

    public event Action? DockContextMenuRequested;

    internal DockState DockState => _dockStateMachine.State;
    internal DockVisualFrame CurrentDockVisualFrame => _currentDockVisualFrame;
    internal bool DockEnabled => _dockStateMachine.Enabled;
    internal DockHandleRenderBackend DockRenderBackend =>
        _dockHandleController?.Backend ?? DockHandleRenderBackend.Unavailable;

    internal DockRuntimeSnapshot CaptureDockRuntimeSnapshot()
    {
        WpfRect physical = CurrentPhysicalBounds();
        PixelMonitorInfo monitor = _dockMonitorService.FromPoint(physical.TopLeft);
        return new DockRuntimeSnapshot(
            DockState,
            _dockEdge,
            _dockCorner,
            Math.Clamp(_dockNormalizedPosition, 0, 1),
            _dockSize,
            _dockWorkArea,
            monitor.Bounds,
            monitor.WorkingArea,
            _dockDpi,
            new WpfRect(Left, Top, Width, Height),
            physical,
            ResizeMode,
            IsVisible,
            Topmost,
            _dockStateMachine.Enabled,
            _settings.MainWindowDocked,
            DockRenderBackend);
    }

    internal void InitializeDockingInteraction()
    {
        if (_dockingInitialized)
        {
            return;
        }

        _dockingInitialized = true;
        _dockWindowAdapter = new DockWindowAdapter(this);
        _dockHandleController = new DockHandleWindowController(this, this);
        WindowChrome.SetIsHitTestVisibleInChrome(WindowRoot, true);
        WindowChrome.SetIsHitTestVisibleInChrome(DockLayer, true);
        WindowRoot.MouseMove += OnPointerMouseMove;
        WindowRoot.MouseLeftButtonUp += OnPointerMouseLeftButtonUp;
        WindowRoot.LostMouseCapture += OnPointerLostMouseCapture;
        LocationChanged += OnDockWindowLocationChanged;
        SizeChanged += OnDockWindowSizeChanged;
        StateChanged += OnDockWindowStateChanged;
        Deactivated += OnDockWindowDeactivated;
        IsVisibleChanged += OnDockWindowVisibilityChanged;
        Loaded += OnDockWindowLoaded;
        ThemePreferences.PaletteChanged += OnDockPaletteChanged;
        ApplyExpandedVisualState();
    }

    public void ConfigureWindowStatePersistence(Func<AppSettings, Task> persistWindowStateAsync)
    {
        ArgumentNullException.ThrowIfNull(persistWindowStateAsync);
        _persistWindowStateAsync = persistWindowStateAsync;
    }

    public void InitializeFromSettingsAndShow(AppSettings settings)
    {
        InitializeFromSettings(settings);
        if (!IsVisible)
        {
            Show();
        }
    }

    internal void InitializeFromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        InitializeDockingInteraction();
        _dockStateMachine.Show();
        ApplyDockSizeSetting(settings.MainWindowDockSize, persist: false);
        _dockStateMachine.Enable();
        if (!settings.MainWindowDockEnabled)
        {
            _dockStateMachine.Apply(DockStateEvent.Disable);
        }

        LoadExpandedBounds(settings);
        Topmost = settings.MainWindowTopmost;
        if (settings.MainWindowDockEnabled && settings.MainWindowDocked)
        {
            PrepareDockedStartup(settings);
        }
        else
        {
            PrepareExpandedStartup(settings);
            _expandedStartupSyncPending = true;
        }
    }

    public void CopyRuntimeWindowStateTo(AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_dockStateMachine.Enabled && DockState is DockState.Docked or DockState.DockPreview)
        {
            target.MainWindowDocked = true;
            target.MainWindowDockEdge = _dockEdge;
            target.MainWindowDockPosition = Math.Clamp(_dockNormalizedPosition, 0, 1);
            target.MainWindowDockWorkAreaX = (int)Math.Round(_dockWorkArea.X);
            target.MainWindowDockWorkAreaY = (int)Math.Round(_dockWorkArea.Y);
            target.MainWindowDockWorkAreaWidth = (int)Math.Round(_dockWorkArea.Width);
            target.MainWindowDockWorkAreaHeight = (int)Math.Round(_dockWorkArea.Height);
        }
        else
        {
            target.MainWindowDocked = false;
        }

        if (_hasExpandedBounds)
        {
            target.MainWindowHasExpandedBounds = true;
            target.MainWindowExpandedX = (int)Math.Round(_expandedPosition.X);
            target.MainWindowExpandedY = (int)Math.Round(_expandedPosition.Y);
            target.MainWindowExpandedWidth = _expandedWidth;
            target.MainWindowExpandedHeight = _expandedHeight;
        }

        target.MainWindowDockSize = (int)Math.Round(_dockSize);
        target.MainWindowDockEnabled = _dockStateMachine.Enabled;
        target.MainWindowTopmost = Topmost;
    }

    internal void ApplyDockSizeSetting(int requestedSize, bool persist = true)
    {
        int size = Math.Clamp(
            requestedSize,
            AppSettings.MinimumMainWindowDockSize,
            AppSettings.MaximumMainWindowDockSize);
        _dockSize = size;
        _settings.MainWindowDockSize = size;

        DockVisualFrame edgeFrame = DockVisualLayoutCalculator.ForEdge(_dockEdge, _dockSize);
        if (DockState is DockState.Docked or DockState.DockPreview)
        {
            DockVisualTarget target = DockVisualLayoutCalculator.TargetFromNormalized(
                _dockEdge,
                _dockWorkArea,
                Math.Max(0.01, _dockDpi.ScaleX),
                _dockNormalizedPosition,
                _dockSize);
            SetDockAnimationTarget(target);
            if (_dockAnimation is null)
            {
                CommitDockTarget(target, synchronizeWpf: true);
            }
        }
        else
        {
            _dockCorner = DockCorner.None;
            _animationTargetDockVisualFrame = edgeFrame;
            ApplyDockVisualFrame(edgeFrame);
        }

        if (persist)
        {
            PersistRuntimeWindowState();
        }
    }

    internal void ApplyDockEnabledSetting(bool enabled)
    {
        _settings.MainWindowDockEnabled = enabled;
        if (enabled)
        {
            _dockStateMachine.Enable();
            return;
        }

        DockStateTransition transition = _dockStateMachine.Apply(DockStateEvent.Disable);
        _finalizeDockWhenAnimationCompletes = false;
        if (transition.RestoreExpanded || DockState == DockState.Expanded)
        {
            RestoreExpandedImmediately();
        }
    }

    private void LoadExpandedBounds(AppSettings settings)
    {
        if (!settings.MainWindowHasExpandedBounds)
        {
            return;
        }

        _hasExpandedBounds = true;
        _expandedPosition = new WpfPoint(settings.MainWindowExpandedX, settings.MainWindowExpandedY);
        _expandedWidth = Math.Max(ExpandedMinWidth, settings.MainWindowExpandedWidth);
        _expandedHeight = Math.Max(ExpandedMinHeight, settings.MainWindowExpandedHeight);
    }

    private void PrepareExpandedStartup(AppSettings settings)
    {
        _dockStateMachine.Apply(DockStateEvent.CompleteRestore);
        ApplyExpandedVisualState();
        if (!_hasExpandedBounds)
        {
            return;
        }

        MonitorSnapshot screen = MonitorForPoint(_expandedPosition);
        (WpfPoint position, double width, double height) bounds = ClampExpandedBounds(
            screen,
            _expandedPosition,
            _expandedWidth,
            _expandedHeight);
        SetExpandedBoundsDip(bounds.position, bounds.width, bounds.height, screen.Info.Dpi);
        SaveExpandedBounds(bounds.position, bounds.width, bounds.height);
    }

    private void PrepareDockedStartup(AppSettings settings)
    {
        _dockHandleController?.BeginSession();
        WpfRect savedArea = new(
            settings.MainWindowDockWorkAreaX,
            settings.MainWindowDockWorkAreaY,
            settings.MainWindowDockWorkAreaWidth,
            settings.MainWindowDockWorkAreaHeight);
        MonitorSnapshot screen = MonitorSelection.Select(_dockMonitorService.GetAll(), savedArea)
            ?? _dockMonitorService.Primary;
        _dockEdge = Enum.IsDefined(settings.MainWindowDockEdge)
            ? settings.MainWindowDockEdge
            : MainWindowDockEdge.Left;
        _dockNormalizedPosition = Math.Clamp(settings.MainWindowDockPosition, 0, 1);
        _dockWorkArea = screen.Info.WorkingArea;
        _dockDpi = screen.Info.Dpi;
        DockVisualTarget target = DockVisualLayoutCalculator.TargetFromNormalized(
            _dockEdge,
            _dockWorkArea,
            Math.Max(0.01, _dockDpi.ScaleX),
            _dockNormalizedPosition,
            _dockSize);
        SetDockAnimationTarget(target);
        MinWidth = _dockSize;
        MinHeight = _dockSize;
        ResizeMode = ResizeMode.NoResize;
        Width = target.Frame.Width;
        Height = target.Frame.Height;
        SetWindowPositionDip(target.Position, _dockDpi);
        _dockStateMachine.Apply(DockStateEvent.BeginPreview);
        _dockStateMachine.Apply(DockStateEvent.CommitPreview);
        SetDockedVisualState();
        SetDockVisualScale(DockIdleScale);
        CommitDockTarget(target, synchronizeWpf: true);
    }

    private void SynchronizeExpandedStartupBounds()
    {
        if (!_expandedStartupSyncPending || DockState != DockState.Expanded)
        {
            return;
        }

        _expandedStartupSyncPending = false;
        MonitorSnapshot screen = _hasExpandedBounds
            ? MonitorForPoint(_expandedPosition)
            : _dockMonitorService.FromWindow(this).ToSnapshot();
        WpfPoint position = _hasExpandedBounds ? _expandedPosition : CurrentPhysicalBounds().TopLeft;
        (WpfPoint clamped, double width, double height) = ClampExpandedBounds(
            screen,
            position,
            _hasExpandedBounds ? _expandedWidth : Width,
            _hasExpandedBounds ? _expandedHeight : Height);
        _isSynchronizingExpandedStartupBounds = true;
        try
        {
            SetExpandedBoundsDip(clamped, width, height, screen.Info.Dpi);
            CommitPhysicalBounds(clamped, width, height, screen.Info.Dpi, null, synchronizeWpf: true);
            SaveExpandedBounds(clamped, width, height);
        }
        finally
        {
            _isSynchronizingExpandedStartupBounds = false;
        }
    }

    private void BeginTitleBarDrag(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || DockState != DockState.Expanded)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject original && FindParent<WpfButton>(original) is not null)
        {
            return;
        }

        InitializeDockingInteraction();
        CancelWindowTransitionForInteraction();
        SaveCurrentExpandedBounds();
        _titleDragging = true;
        _latestPointerScreen = CursorScreenPosition(e);
        WpfRect current = CurrentPhysicalBounds();
        _pointerGrabOffset = new WpfPoint(
            _latestPointerScreen.X - current.Left,
            _latestPointerScreen.Y - current.Top);
        _useTitleBarCenterDragAnchor = false;
        if (!Mouse.Capture(WindowRoot, CaptureMode.Element))
        {
            HandleInterruptedInteraction(DockStateEvent.CaptureLost);
        }
        else
        {
            StartPointerReleaseWatch();
        }

        e.Handled = true;
    }

    private void OnPointerMouseMove(object sender, MouseEventArgs e)
    {
        if (_dockDragging)
        {
            OnDockMouseMove(sender, e);
        }
        else if (_titleDragging)
        {
            OnTitleBarMouseMove(sender, e);
        }
    }

    private void OnPointerMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dockDragging)
        {
            OnDockMouseLeftButtonUp(sender, e);
        }
        else if (_titleDragging)
        {
            OnTitleBarMouseLeftButtonUp(sender, e);
        }
    }

    private void OnPointerLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_titleDragging || _dockDragging)
        {
            HandleInterruptedInteraction(DockStateEvent.CaptureLost);
        }
    }

    private void OnTitleBarMouseMove(object sender, MouseEventArgs e)
    {
        if (!_titleDragging)
        {
            return;
        }

        // 预览切换期间输入会积压：松手后的 move 仍带着按下状态排队到达，而实时
        // 读取的指针位置已是松手后的新位置；以物理按键判定松手，避免被瞬间拉出。
        bool physicalKnown = _dockMonitorService.TryGetCursorState(out WpfPoint pointer, out bool physicalPressed);
        bool leftButtonPressed = DockPointerGate.IsDragPressed(
            Mouse.LeftButton == MouseButtonState.Pressed,
            physicalKnown,
            physicalPressed);
        ProcessTitlePointerMove(physicalKnown ? pointer : CursorScreenPosition(e), leftButtonPressed);
        e.Handled = true;
    }

    private void ProcessTitlePointerMove(WpfPoint pointer, bool leftButtonPressed)
    {
        if (!_titleDragging)
        {
            return;
        }

        if (!leftButtonPressed)
        {
            CompletePointerInteraction();
            return;
        }

        _latestPointerScreen = pointer;
        if (DockState == DockState.Expanded)
        {
            if (_useTitleBarCenterDragAnchor)
            {
                MoveExpandedFromDock(_latestPointerScreen);
            }
            else
            {
                MoveExpandedWindow(_latestPointerScreen);
            }

            if (TryFindDockEdge(_latestPointerScreen, out MainWindowDockEdge edge, out MonitorSnapshot monitor))
            {
                BeginDockPreview(edge, monitor, _latestPointerScreen);
            }
        }
        else if (DockState == DockState.DockPreview)
        {
            if (TryFindDockEdge(_latestPointerScreen, out MainWindowDockEdge edge, out MonitorSnapshot monitor))
            {
                UpdateDockPreviewTarget(edge, monitor, _latestPointerScreen);
            }
            else if (IsPastDetachThreshold(_latestPointerScreen, _dockEdge, _dockWorkArea))
            {
                BeginRestoreDrag(_latestPointerScreen);
            }
        }
        else if (DockState == DockState.RestoreDrag)
        {
            UpdateRestoreDragTarget(_latestPointerScreen);
            if (TryFindDockEdge(_latestPointerScreen, out MainWindowDockEdge edge, out MonitorSnapshot monitor))
            {
                BeginDockPreview(edge, monitor, _latestPointerScreen);
            }
        }
    }

    private void OnTitleBarMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_titleDragging || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _latestPointerScreen = CursorScreenPosition(e);
        CompletePointerInteraction();
        e.Handled = true;
    }

    private void OnDockMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DockState != DockState.Docked || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _dockDragging = true;
        _dockDragStarted = false;
        _dockRightButtonPending = false;
        _useTitleBarCenterDragAnchor = false;
        _dockPressScreen = CursorScreenPosition(e);
        _latestPointerScreen = _dockPressScreen;
        if (!Mouse.Capture(WindowRoot, CaptureMode.Element))
        {
            HandleInterruptedInteraction(DockStateEvent.CaptureLost);
        }
        else
        {
            StartPointerReleaseWatch();
        }

        e.Handled = true;
    }

    private void OnDockMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dockDragging)
        {
            return;
        }

        // 同 OnTitleBarMouseMove：以物理按键判定松手，避免积压的 move 把贴边窗口瞬间拉出。
        bool physicalKnown = _dockMonitorService.TryGetCursorState(out WpfPoint pointer, out bool physicalPressed);
        if (!DockPointerGate.IsDragPressed(
                Mouse.LeftButton == MouseButtonState.Pressed,
                physicalKnown,
                physicalPressed))
        {
            CompletePointerInteraction();
            e.Handled = true;
            return;
        }

        _latestPointerScreen = physicalKnown ? pointer : CursorScreenPosition(e);
        ProcessDockPointerMove(_latestPointerScreen, leftButtonPressed: true);
        e.Handled = true;
    }

    private void ProcessDockPointerMove(WpfPoint pointer, bool leftButtonPressed)
    {
        if (!_dockDragging)
        {
            return;
        }

        if (!leftButtonPressed)
        {
            CompletePointerInteraction();
            return;
        }

        _latestPointerScreen = pointer;
        if (!_dockDragStarted)
        {
            if (!HasReachedDockDragThreshold(
                    _dockPressScreen,
                    _latestPointerScreen,
                    Math.Max(0.01, _dockDpi.ScaleX)))
            {
                return;
            }

            _dockDragStarted = true;
            AnimateDockVisualScale(DockActiveScale);
        }

        if (DockState == DockState.RestoreDrag)
        {
            UpdateRestoreDragTarget(_latestPointerScreen);
            if (TryFindDockEdge(_latestPointerScreen, out MainWindowDockEdge edge, out MonitorSnapshot monitor))
            {
                BeginDockPreview(edge, monitor, _latestPointerScreen);
            }

            return;
        }

        if (TryFindDockEdge(_latestPointerScreen, out MainWindowDockEdge nextEdge, out MonitorSnapshot nextMonitor))
        {
            _dockEdge = nextEdge;
            _dockWorkArea = nextMonitor.Info.WorkingArea;
            _dockDpi = nextMonitor.Info.Dpi;
        }
        else if (IsPastDetachThreshold(_latestPointerScreen, _dockEdge, _dockWorkArea))
        {
            _useTitleBarCenterDragAnchor = true;
            BeginRestoreDrag(_latestPointerScreen);
            return;
        }

        DockVisualTarget target = DockVisualLayoutCalculator.TargetFromCursor(
            _dockEdge,
            _dockWorkArea,
            Math.Max(0.01, _dockDpi.ScaleX),
            _latestPointerScreen,
            _dockSize);
        DockCorner previousCorner = _dockCorner;
        SetDockAnimationTarget(target);
        if (previousCorner != target.Corner)
        {
            StartDockAnimation(
                target.Position,
                target.Frame.Width,
                target.Frame.Height,
                0,
                1,
                target.Frame,
                completed: null,
                requestedDuration: MotionPreferences.FastDuration);
        }
        else if (_dockAnimation is null)
        {
            CommitDockTarget(target, synchronizeWpf: true);
        }

    }

    private void OnDockMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dockDragging || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        CompletePointerInteraction();
        e.Handled = true;
    }

    private void OnDockMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DockState == DockState.Docked && e.ChangedButton == MouseButton.Right)
        {
            _dockRightButtonPending = true;
            e.Handled = true;
        }
    }

    private void OnDockMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right)
        {
            return;
        }

        bool showMenu = _dockRightButtonPending && DockState == DockState.Docked && !_dockDragging;
        _dockRightButtonPending = false;
        if (showMenu)
        {
            DockContextMenuRequested?.Invoke();
        }

        if (DockState == DockState.Docked || showMenu)
        {
            e.Handled = true;
        }
    }

    private void BeginDockPreview(MainWindowDockEdge edge, MonitorSnapshot monitor, WpfPoint cursor)
    {
        if (!_dockStateMachine.Enabled)
        {
            return;
        }

        if (DockState == DockState.Expanded)
        {
            MeasureTitleBarDragCenter();
            SaveCurrentExpandedBounds();
            _dockStateMachine.Apply(DockStateEvent.BeginPreview);
        }
        else if (DockState == DockState.RestoreDrag)
        {
            _restoreCompletion = null;
            _dockStateMachine.Apply(DockStateEvent.BeginPreview);
        }

        _finalizeDockWhenAnimationCompletes = false;
        _dockHandleController?.BeginSession();
        UpdateTaskbarIconVisibility(IsVisible);
        AnimateDockVisualScale(DockActiveScale);
        MinWidth = _dockSize;
        MinHeight = _dockSize;
        ResizeMode = ResizeMode.NoResize;
        UpdateDockPreviewTarget(edge, monitor, cursor, forceAnimation: true);
    }

    private void UpdateDockPreviewTarget(
        MainWindowDockEdge edge,
        MonitorSnapshot monitor,
        WpfPoint cursor,
        bool forceAnimation = false)
    {
        bool edgeChanged = edge != _dockEdge;
        DockCorner previousCorner = _dockCorner;
        _dockEdge = edge;
        _dockWorkArea = monitor.Info.WorkingArea;
        _dockDpi = monitor.Info.Dpi;
        DockVisualTarget target = DockVisualLayoutCalculator.TargetFromCursor(
            edge,
            _dockWorkArea,
            Math.Max(0.01, _dockDpi.ScaleX),
            cursor,
            _dockSize);
        SetDockAnimationTarget(target);
        bool sameAnimatedTargetKind = _dockAnimation is not null
            && !forceAnimation
            && !edgeChanged
            && previousCorner == target.Corner;
        if (sameAnimatedTargetKind)
        {
            return;
        }

        bool compactVisual = DockLayer.Opacity >= 0.999 && ExpandedSurface.Opacity <= 0.001;
        if (!forceAnimation
            && !edgeChanged
            && previousCorner == target.Corner
            && compactVisual)
        {
            CommitDockTarget(target, synchronizeWpf: true);
            return;
        }

        bool changed = forceAnimation
            || edgeChanged
            || previousCorner != target.Corner
            || !NativeBoundsAreClose(CurrentPhysicalBounds(), target.Position, target.Frame.Width, target.Frame.Height, _dockDpi);
        if (changed)
        {
            StartDockAnimation(
                target.Position,
                target.Frame.Width,
                target.Frame.Height,
                0,
                1,
                target.Frame,
                CompleteDockPreviewAnimation,
                edgeChanged || previousCorner != target.Corner
                    ? MotionPreferences.FastDuration
                    : MotionPreferences.DockDuration);
        }
    }

    private void SetDockAnimationTarget(DockVisualTarget target)
    {
        _dockCorner = target.Corner;
        _dockNormalizedPosition = Math.Clamp(target.NormalizedPosition, 0, 1);
        _animationTargetPosition = target.Position;
        _animationTargetWidth = target.Frame.Width;
        _animationTargetHeight = target.Frame.Height;
        _animationTargetDockVisualFrame = target.Frame;
    }

    private void CompleteDockPreviewAnimation()
    {
        _dockAnimation = null;
        if (!_dockStateMachine.Enabled)
        {
            RestoreExpandedImmediately();
            return;
        }

        bool pointerHeld = _titleDragging || _dockDragging;
        if (pointerHeld)
        {
            // 动画完成回调（Render 优先级）可能先于排队的松手事件执行：
            // 以物理按键为准，松手即直接落定贴边，不再保留拖动会话。
            bool physicalKnown = _dockMonitorService.TryGetCursorState(out _, out bool physicalPressed);
            pointerHeld = DockPointerGate.IsDragPressed(pointerHeld, physicalKnown, physicalPressed);
        }

        if (_finalizeDockWhenAnimationCompletes || !pointerHeld)
        {
            FinalizeDock();
        }
        else
        {
            // The pointer is still held. Keep the existing title/dock drag
            // session alive while the layered handle becomes the visible
            // surface; releasing the button will finalize Docked normally.
            SetDockedVisualState(preserveActiveDrag: true);
        }
    }

    private void FinalizeDock()
    {
        if (!_dockStateMachine.Enabled)
        {
            RestoreExpandedImmediately();
            return;
        }

        _dockAnimation?.Cancel();
        _dockAnimation = null;
        if (DockState == DockState.DockPreview)
        {
            _dockStateMachine.Apply(DockStateEvent.CommitPreview);
        }

        CommitPhysicalBounds(
            _animationTargetPosition,
            _animationTargetWidth,
            _animationTargetHeight,
            _dockDpi,
            _animationTargetDockVisualFrame.Outline,
            synchronizeWpf: true);
        ApplyDockVisualFrame(_animationTargetDockVisualFrame);
        SetDockedVisualState();
        SetDockVisualScale(DockIdleScale);
        _finalizeDockWhenAnimationCompletes = false;
        PersistRuntimeWindowState();
    }

    private void BeginRestoreDrag(WpfPoint? cursor)
    {
        if (DockState != DockState.Docked && DockState != DockState.DockPreview)
        {
            return;
        }

        _finalizeDockWhenAnimationCompletes = false;
        if (DockState == DockState.DockPreview)
        {
            _dockStateMachine.Apply(DockStateEvent.LeavePreview);
        }
        else
        {
            _dockStateMachine.Apply(DockStateEvent.BeginRestoreDrag);
        }

        _useTitleBarCenterDragAnchor = cursor.HasValue;
        _dockAnimation?.Cancel();
        _dockAnimation = null;
        _dockWindowAdapter?.ClearRegion();
        _dockWindowAdapter?.SetInputTransparent(false);
        _dockWindowAdapter?.Unpark();
        Opacity = 1;
        SetCustomNativeRegionActive(false);

        MonitorSnapshot monitor = cursor.HasValue
            ? MonitorForPoint(cursor.Value)
            : MonitorForPoint(_expandedPosition);
        _dockDpi = monitor.Info.Dpi;
        (WpfPoint position, double width, double height) bounds;
        if (cursor.HasValue)
        {
            bounds = ExpandedBoundsForCursor(cursor.Value, monitor);
        }
        else
        {
            bounds = ClampExpandedBounds(
                monitor,
                _expandedPosition,
                _expandedWidth,
                _expandedHeight);
        }

        StartDockAnimation(
            bounds.position,
            bounds.width,
            bounds.height,
            1,
            0,
            _currentDockVisualFrame,
            CompleteRestore,
            MotionPreferences.FastDuration);
    }

    private void RestoreExpandedWithTransition(Action? completed)
    {
        if (DockState == DockState.RestoreDrag)
        {
            if (completed is not null)
            {
                _restoreCompletion = completed;
            }

            if (_dockAnimation is null)
            {
                CompleteRestore();
            }

            return;
        }

        if (DockState is not (DockState.Docked or DockState.DockPreview))
        {
            completed?.Invoke();
            return;
        }

        _restoreCompletion = completed;
        BeginRestoreDrag(cursor: null);
    }

    private void CompleteRestore()
    {
        bool continuePointerDrag = _titleDragging || _dockDragging;
        CompleteRestoreState(continuePointerDrag);
    }

    private void CompleteRestoreState(bool continuePointerDrag)
    {
        if (continuePointerDrag)
        {
            // 还原动画完成回调可能先于排队的松手事件执行：
            // 松手后不得把窗口瞬移到指针位置接回拖动。
            bool physicalKnown = _dockMonitorService.TryGetCursorState(out _, out bool physicalPressed);
            if (!DockPointerGate.IsDragPressed(continuePointerDrag, physicalKnown, physicalPressed))
            {
                continuePointerDrag = false;
            }
        }

        if (DockState == DockState.RestoreDrag)
        {
            _dockStateMachine.Apply(DockStateEvent.CompleteRestore);
        }

        _dockDragging = false;
        _titleDragging = continuePointerDrag;
        MinWidth = ExpandedMinWidth;
        MinHeight = ExpandedMinHeight;
        ResizeMode = ResizeMode.CanResize;
        ApplyExpandedVisualState();
        SetDockVisualScale(DockIdleScale);
        if (continuePointerDrag)
        {
            MoveExpandedFromDock(_latestPointerScreen);
        }
        else
        {
            ReleasePointerCapture();
            _useTitleBarCenterDragAnchor = false;
            SaveCurrentExpandedBounds();
            PersistRuntimeWindowState();
        }

        CompleteRestoreRequest();
    }

    private void RestoreExpandedImmediately()
    {
        _dockAnimation?.Cancel();
        _dockAnimation = null;
        _finalizeDockWhenAnimationCompletes = false;
        _useTitleBarCenterDragAnchor = false;
        if (DockState is DockState.Docked or DockState.DockPreview or DockState.RestoreDrag)
        {
            if (DockState == DockState.DockPreview)
            {
                _dockStateMachine.Apply(DockStateEvent.LeavePreview);
            }

            if (DockState == DockState.RestoreDrag)
            {
                _dockStateMachine.Apply(DockStateEvent.CompleteRestore);
            }

            if (DockState == DockState.Docked)
            {
                _dockStateMachine.Apply(DockStateEvent.BeginRestoreDrag);
                _dockStateMachine.Apply(DockStateEvent.CompleteRestore);
            }
        }

        MonitorSnapshot monitor = MonitorForPoint(_expandedPosition);
        (WpfPoint position, double width, double height) bounds = ClampExpandedBounds(
            monitor,
            _expandedPosition,
            _expandedWidth,
            _expandedHeight);
        CommitPhysicalBounds(bounds.position, bounds.width, bounds.height, monitor.Info.Dpi, null, synchronizeWpf: true);
        ApplyExpandedVisualState();
        SaveExpandedBounds(bounds.position, bounds.width, bounds.height);
        PersistRuntimeWindowState();
        CompleteRestoreRequest();
    }

    private void CompleteRestoreRequest()
    {
        Action? completed = _restoreCompletion;
        _restoreCompletion = null;
        if (IsVisible)
        {
            completed?.Invoke();
        }
    }

    private void CompletePointerInteraction()
    {
        StopPointerReleaseWatch();
        _titleDragging = false;
        _dockDragging = false;
        _dockDragStarted = false;
        ReleasePointerCapture();
        AnimateDockVisualScale(DockIdleScale);

        if (DockState == DockState.DockPreview)
        {
            _useTitleBarCenterDragAnchor = false;
            _finalizeDockWhenAnimationCompletes = true;
            if (_dockAnimation is null)
            {
                FinalizeDock();
            }
        }
        else if (DockState == DockState.RestoreDrag)
        {
            if (_dockAnimation is null)
            {
                CompleteRestore();
            }
        }
        else if (DockState == DockState.Docked)
        {
            _useTitleBarCenterDragAnchor = false;
            PersistRuntimeWindowState();
        }
        else
        {
            _useTitleBarCenterDragAnchor = false;
            SaveCurrentExpandedBounds();
            PersistRuntimeWindowState();
        }
    }

    private void StartDockAnimation(
        WpfPoint targetPosition,
        double targetWidth,
        double targetHeight,
        double targetExpandedOpacity,
        double targetDockOpacity,
        DockVisualFrame targetFrame,
        Action? completed,
        TimeSpan? requestedDuration = null)
    {
        _dockAnimation?.Cancel();
        WpfRect actual = CurrentPhysicalBounds();
        DpiScale2 actualDpi = _dockMonitorService.FromPoint(actual.TopLeft).Dpi;
        _animationFromPosition = actual.TopLeft;
        _animationFromWidth = actual.Width / Math.Max(0.01, actualDpi.ScaleX);
        _animationFromHeight = actual.Height / Math.Max(0.01, actualDpi.ScaleY);
        _animationFromExpandedOpacity = ExpandedSurface.Opacity;
        _animationFromDockOpacity = DockLayer.Opacity;
        _animationFromDockVisualFrame = _currentDockVisualFrame;
        _animationTargetPosition = targetPosition;
        _animationTargetWidth = targetWidth;
        _animationTargetHeight = targetHeight;
        _animationTargetExpandedOpacity = targetExpandedOpacity;
        _animationTargetDockOpacity = targetDockOpacity;
        _animationTargetDockVisualFrame = targetFrame;
        bool compactToCompact = _animationFromDockOpacity >= 0.999 && targetDockOpacity >= 0.999;
        if (targetDockOpacity > 0.001 && DockRenderBackend != DockHandleRenderBackend.Layered)
        {
            SetResourceReference(BackgroundProperty, "SurfacePrimaryBrush");
            ShellBackground = TryFindResource("SurfacePrimaryBrush") as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.White;
            ShellBorderBrush = System.Windows.Media.Brushes.Transparent;
        }
        PrepareExpandedSurfaceForAnimation();
        if (targetDockOpacity > 0.001)
        {
            _dockWindowAdapter?.ClearRegion();
            SetCustomNativeRegionActive(true);
        }
        else if (!compactToCompact)
        {
            _dockWindowAdapter?.ClearRegion();
            SetCustomNativeRegionActive(false);
        }

        bool final = NativeBoundsAreClose(actual, targetPosition, targetWidth, targetHeight, _dockDpi)
            && _animationFromDockVisualFrame == targetFrame
            && Math.Abs(_animationFromExpandedOpacity - targetExpandedOpacity) < 0.001
            && Math.Abs(_animationFromDockOpacity - targetDockOpacity) < 0.001;
        TimeSpan duration = final
            ? TimeSpan.Zero
            : requestedDuration ?? MotionPreferences.DockDuration;
        FrameAnimation? animation = null;
        animation = new FrameAnimation();
        _dockAnimation = animation;
        animation.Start(
            duration,
            MotionEasing.CubicEaseOut,
            progress =>
            {
                double width = DockVisualLayoutCalculator.Lerp(_animationFromWidth, _animationTargetWidth, progress);
                double height = DockVisualLayoutCalculator.Lerp(_animationFromHeight, _animationTargetHeight, progress);
                double expandedOpacity = Lerp(_animationFromExpandedOpacity, _animationTargetExpandedOpacity, progress);
                double dockOpacity = Lerp(_animationFromDockOpacity, _animationTargetDockOpacity, progress);
                DockVisualFrame frame = DockVisualFrame.Lerp(
                    _animationFromDockVisualFrame,
                    _animationTargetDockVisualFrame,
                    progress);
                MonitorSnapshot pointerMonitor = MonitorForPoint(_latestPointerScreen);
                // Restoring stays attached to the pointer so the expanded title bar
                // remains under the active drag. Entering DockPreview instead uses
                // the same progress as size/shape interpolation, allowing the window
                // to approach the edge continuously instead of snapping there after
                // the compact transformation has already completed.
                bool pointerAnchored = ShouldAnchorDockAnimationToPointer(
                    DockState,
                    _titleDragging,
                    _dockDragging,
                    compactToCompact);
                WpfPoint position = pointerAnchored
                    ? PointerAnchoredAnimationPosition(
                        _latestPointerScreen,
                        width,
                        frame,
                        dockOpacity,
                        pointerMonitor)
                    : new WpfPoint(
                        Math.Round(DockVisualLayoutCalculator.Lerp(_animationFromPosition.X, _animationTargetPosition.X, progress)),
                        Math.Round(DockVisualLayoutCalculator.Lerp(_animationFromPosition.Y, _animationTargetPosition.Y, progress)));
                DpiScale2 dpi = pointerAnchored
                    ? pointerMonitor.Info.Dpi
                    : _dockMonitorService.FromPoint(position).Dpi;
                CommitPhysicalBounds(
                    position,
                    width,
                    height,
                    dpi,
                    compactToCompact ? frame.Outline : null,
                    synchronizeWpf: false);
                ExpandedSurface.Opacity = expandedOpacity;
                DockLayer.Opacity = dockOpacity;
                _dockVisualOpacity = dockOpacity;
                DockLayer.Visibility = DockRenderBackend == DockHandleRenderBackend.Layered && dockOpacity > 0.001
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                SetExpandedSurfaceScale(
                    Lerp(_animationFromSurfaceScaleX, _animationTargetSurfaceScaleX, progress),
                    Lerp(_animationFromSurfaceScaleY, _animationTargetSurfaceScaleY, progress));
                ApplyDockVisualFrame(frame);
            },
            () =>
            {
                if (!ReferenceEquals(_dockAnimation, animation))
                {
                    return;
                }

                CommitPhysicalBounds(
                    _animationTargetPosition,
                    _animationTargetWidth,
                    _animationTargetHeight,
                    _dockDpi,
                    _animationTargetDockOpacity >= 0.999
                        ? _animationTargetDockVisualFrame.Outline
                        : null,
                    synchronizeWpf: true);
                ExpandedSurface.Opacity = _animationTargetExpandedOpacity;
                DockLayer.Opacity = _animationTargetDockOpacity;
                _dockVisualOpacity = _animationTargetDockOpacity;
                DockLayer.Visibility = DockRenderBackend == DockHandleRenderBackend.Layered
                    && _animationTargetDockOpacity > 0.001
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                SetExpandedSurfaceScale(_animationTargetSurfaceScaleX, _animationTargetSurfaceScaleY);
                ApplyDockVisualFrame(_animationTargetDockVisualFrame);
                _dockAnimation = null;
                animation.Dispose();
                completed?.Invoke();
            });
    }

    private void CommitDockTarget(DockVisualTarget target, bool synchronizeWpf)
    {
        CommitPhysicalBounds(
            target.Position,
            target.Frame.Width,
            target.Frame.Height,
            _dockDpi,
            target.Frame.Outline,
            synchronizeWpf);
        ApplyDockVisualFrame(target.Frame);
    }

    private void SetDockedVisualState(
        bool allowHitTesting = true,
        bool preserveActiveDrag = false)
    {
        UpdateTaskbarIconVisibility(IsVisible);
        if (DockRenderBackend == DockHandleRenderBackend.Layered)
        {
            Background = System.Windows.Media.Brushes.Transparent;
            ClearValue(ShellBackgroundProperty);
            ShellBorderBrush = System.Windows.Media.Brushes.Transparent;
        }
        else
        {
            SetResourceReference(BackgroundProperty, "SurfacePrimaryBrush");
            ShellBackground = TryFindResource("SurfacePrimaryBrush") as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.White;
            ShellBorderBrush = System.Windows.Media.Brushes.Transparent;
        }
        MinWidth = _dockSize;
        MinHeight = _dockSize;
        ResizeMode = ResizeMode.NoResize;
        ExpandedSurface.Opacity = 0;
        ExpandedSurface.IsHitTestVisible = false;
        DockLayer.Opacity = 1;
        _dockVisualOpacity = 1;
        DockLayer.IsHitTestVisible = allowHitTesting;
        bool layered = DockRenderBackend == DockHandleRenderBackend.Layered;
        DockLayer.Visibility = layered ? Visibility.Collapsed : Visibility.Visible;
        SetCustomNativeRegionActive(layered);
        _dockWindowAdapter?.SetInputTransparent(layered);
        // During an active title-bar drag, keep the WPF window alive for the
        // existing capture chain. It is parked immediately below after the
        // layered bitmap is submitted, so it cannot expose its native
        // background while the pointer remains down.
        Opacity = layered && !preserveActiveDrag ? 0 : 1;
        ApplyDockVisualFrame(_animationTargetDockVisualFrame);
        if (!layered)
        {
            ApplyRegionFallbackVisualState(_animationTargetDockVisualFrame);
        }
        else if (preserveActiveDrag)
        {
            _dockWindowAdapter?.ClearRegion();
            _dockWindowAdapter?.ParkOffscreen();
        }
    }

    private void ApplyExpandedVisualState()
    {
        UpdateTaskbarIconVisibility(IsVisible);
        _dockWindowAdapter?.Unpark();
        Opacity = 1;
        Background = System.Windows.Media.Brushes.Transparent;
        ClearValue(ShellBackgroundProperty);
        ClearValue(ShellBorderBrushProperty);
        _dockWindowAdapter?.ClearRegion();
        MinWidth = ExpandedMinWidth;
        MinHeight = ExpandedMinHeight;
        ResizeMode = ResizeMode.CanResize;
        ExpandedSurface.Width = double.NaN;
        ExpandedSurface.Height = double.NaN;
        ExpandedSurface.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        ExpandedSurface.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
        ExpandedSurface.RenderTransformOrigin = new WpfPoint(0.5, 0.5);
        SetExpandedSurfaceScale(1, 1);
        ExpandedSurface.Opacity = 1;
        ExpandedSurface.IsHitTestVisible = true;
        DockLayer.Opacity = 0;
        _dockVisualOpacity = 0;
        DockLayer.IsHitTestVisible = false;
        DockLayer.Visibility = Visibility.Visible;
        _dockHandleController?.Hide(resetSession: true);
        _dockWindowAdapter?.SetInputTransparent(false);
        SetCustomNativeRegionActive(false);
        DockLayer.Clip = null;
        SetDockVisualScale(DockIdleScale);
    }

    private void ApplyDockVisualFrame(DockVisualFrame frame)
    {
        _currentDockVisualFrame = frame;
        Geometry outlineGeometry = frame.Outline.CreateGeometry();
        DockShape.Data = outlineGeometry;
        DockOutline.Data = outlineGeometry;
        DockLayer.Clip = DockRenderBackend == DockHandleRenderBackend.Layered
            ? null
            : CreateVisibleDockGeometry(frame);
        DockVisual.Width = frame.Width;
        DockVisual.Height = frame.Height;
        DockContent.Width = _dockSize;
        DockContent.Height = _dockSize;
        DockIconSurface.Width = _dockSize;
        DockIconSurface.Height = _dockSize;
        if (DockContent.RenderTransform is not TranslateTransform translate)
        {
            translate = new TranslateTransform();
            DockContent.RenderTransform = translate;
        }

        translate.X = frame.ContentOffset.X;
        translate.Y = frame.ContentOffset.Y;
        if (DockIconSurface.RenderTransform is not TranslateTransform iconTranslate)
        {
            iconTranslate = new TranslateTransform();
            DockIconSurface.RenderTransform = iconTranslate;
        }

        iconTranslate.X = frame.ContentOffset.X;
        iconTranslate.Y = frame.ContentOffset.Y;
        DockVisual.RenderTransformOrigin = frame.ScaleOrigin;
        if (DockRenderBackend == DockHandleRenderBackend.RegionFallback)
        {
            _dockWindowAdapter?.ApplyVisibleOutline(
                frame.Outline,
                _dockDpi,
                CurrentDockVisualScale(),
                frame.ScaleOrigin,
                frame.Width,
                frame.Height);
        }

        SubmitDockHandle(frame, _dockVisualOpacity);
    }

    private void SubmitDockHandle(DockVisualFrame frame, double opacity)
    {
        if (_dockHandleController is null
            || DockRenderBackend != DockHandleRenderBackend.Layered)
        {
            return;
        }

        if (opacity <= 0.001)
        {
            _dockHandleController.Hide();
            return;
        }

        System.Windows.Media.Brush fill = TryFindResource("SurfacePrimaryBrush") as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.White;
        System.Windows.Media.Brush stroke = TryFindResource("BorderSubtleBrush") as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.Transparent;
        WpfRect currentBounds = CurrentPhysicalBounds();
        bool submitted = _dockHandleController.Submit(new DockHandlePresentation(
            currentBounds.TopLeft,
            frame,
            _dockSize,
            CurrentDockVisualScale(),
            opacity,
            _dockDpi,
            fill,
            stroke,
            Topmost));
        if (!submitted)
        {
            ApplyRegionFallbackVisualState(frame);
            return;
        }

        if (DockState == DockState.Docked
            && opacity >= 0.999
            && ExpandedSurface.Opacity <= 0.001)
        {
            // UpdateLayeredWindow has succeeded, so the anti-aliased handle is
            // now the only surface that needs to remain on-screen. Parking the
            // opaque WPF host removes its native client background and DWM
            // shadow without changing the layered bitmap or its logical bounds.
            _dockWindowAdapter?.ClearRegion();
            _dockWindowAdapter?.ParkOffscreen();
        }
    }

    private void ApplyRegionFallbackVisualState(DockVisualFrame frame)
    {
        _dockHandleController?.Hide();
        _dockWindowAdapter?.Unpark();
        Opacity = 1;
        SetResourceReference(BackgroundProperty, "SurfacePrimaryBrush");
        ShellBackground = TryFindResource("SurfacePrimaryBrush") as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.White;
        ShellBorderBrush = System.Windows.Media.Brushes.Transparent;
        DockLayer.Clip = CreateVisibleDockGeometry(frame);
        DockLayer.Visibility = Visibility.Visible;
        DockLayer.IsHitTestVisible = DockState is DockState.Docked or DockState.DockPreview;
        _dockWindowAdapter?.SetInputTransparent(false);
        SetCustomNativeRegionActive(true);

        WpfRect bounds = CurrentPhysicalBounds();
        _dockWindowAdapter?.Commit(
            bounds.TopLeft,
            frame.Width,
            frame.Height,
            _dockDpi,
            frame.Outline,
            synchronizeWpf: false);
    }

    private void OnDockPaletteChanged(object? sender, EventArgs e)
    {
        if (DockState is DockState.Docked or DockState.DockPreview)
        {
            SubmitDockHandle(_currentDockVisualFrame, _dockVisualOpacity);
        }
    }

    void IDockHandleInputSink.OnDockHandleLeftButtonDown(WpfPoint screenPoint)
    {
        BeginDockInteractionFromLayeredHandle(screenPoint, MouseButton.Left);
    }

    void IDockHandleInputSink.OnDockHandleMouseMove(WpfPoint screenPoint, bool leftButtonPressed)
    {
        // 原生捕获在松手消息处理前不会释放：以物理按键为准，防止松手后的移动触发拖出。
        if (leftButtonPressed)
        {
            bool physicalKnown = _dockMonitorService.TryGetCursorState(out _, out bool physicalPressed);
            leftButtonPressed = DockPointerGate.IsDragPressed(leftButtonPressed, physicalKnown, physicalPressed);
        }

        ProcessDockPointerMove(screenPoint, leftButtonPressed);
    }

    void IDockHandleInputSink.OnDockHandleLeftButtonUp(WpfPoint screenPoint)
    {
        _latestPointerScreen = screenPoint;
        CompletePointerInteraction();
    }

    void IDockHandleInputSink.OnDockHandleRightButtonDown(WpfPoint screenPoint)
    {
        _dockRightButtonPending = DockState == DockState.Docked && !_dockDragging;
    }

    void IDockHandleInputSink.OnDockHandleRightButtonUp(WpfPoint screenPoint)
    {
        if (_dockRightButtonPending && DockState == DockState.Docked && !_dockDragging)
        {
            DockContextMenuRequested?.Invoke();
        }

        _dockRightButtonPending = false;
    }

    void IDockHandleInputSink.OnDockHandleCaptureLost() =>
        HandleInterruptedInteraction(DockStateEvent.CaptureLost);

    void IDockHandleInputSink.OnDockHandleEnvironmentChanged()
    {
        if (DockState is not (DockState.Docked or DockState.DockPreview))
        {
            return;
        }

        MonitorSnapshot monitor = MonitorForPoint(_animationTargetPosition);
        _dockWorkArea = monitor.Info.WorkingArea;
        _dockDpi = monitor.Info.Dpi;
        DockVisualTarget target = DockVisualLayoutCalculator.TargetFromNormalized(
            _dockEdge,
            _dockWorkArea,
            Math.Max(0.01, _dockDpi.ScaleX),
            _dockNormalizedPosition,
            _dockSize);
        SetDockAnimationTarget(target);
        CommitDockTarget(target, synchronizeWpf: true);
    }

    private void BeginDockInteractionFromLayeredHandle(WpfPoint screenPoint, MouseButton button)
    {
        if (button != MouseButton.Left || DockState != DockState.Docked)
        {
            return;
        }

        _dockDragging = true;
        _dockDragStarted = false;
        _dockPressScreen = screenPoint;
        _latestPointerScreen = screenPoint;
        StartPointerReleaseWatch();
        _dockHandleController?.SetTopmost(Topmost);
    }

    private Geometry CreateVisibleDockGeometry(DockVisualFrame frame)
    {
        Geometry geometry = frame.Outline.CreateGeometry().Clone();
        double scale = CurrentDockVisualScale();
        if (Math.Abs(scale - 1) > 0.0001)
        {
            geometry.Transform = new ScaleTransform(
                scale,
                scale,
                frame.ScaleOrigin.X * frame.Width,
                frame.ScaleOrigin.Y * frame.Height);
        }

        geometry.Freeze();
        return geometry;
    }

    private void AnimateDockVisualScale(double targetScale)
    {
        _dockScaleAnimation?.Cancel();
        if (DockVisual.RenderTransform is not ScaleTransform transform)
        {
            return;
        }

        double from = transform.ScaleX;
        if (Math.Abs(from - targetScale) < 0.0001)
        {
            SetDockVisualScale(targetScale);
            return;
        }

        FrameAnimation? animation = null;
        animation = new FrameAnimation();
        _dockScaleAnimation = animation;
        animation.Start(
            MotionPreferences.FastDuration,
            MotionEasing.CubicEaseOut,
            progress => SetDockVisualScale(Lerp(from, targetScale, progress)),
            () =>
            {
                if (ReferenceEquals(_dockScaleAnimation, animation))
                {
                    _dockScaleAnimation = null;
                }

                animation.Dispose();
            });
    }

    private void SetDockVisualScale(double scale)
    {
        if (DockVisual.RenderTransform is ScaleTransform transform)
        {
            transform.ScaleX = scale;
            transform.ScaleY = scale;
        }

        if (DockRenderBackend != DockHandleRenderBackend.Layered
            && _currentDockVisualFrame.Width > 0
            && _currentDockVisualFrame.Height > 0)
        {
            DockLayer.Clip = CreateVisibleDockGeometry(_currentDockVisualFrame);
            _dockWindowAdapter?.ApplyVisibleOutline(
                _currentDockVisualFrame.Outline,
                _dockDpi,
                scale,
                _currentDockVisualFrame.ScaleOrigin,
                _currentDockVisualFrame.Width,
                _currentDockVisualFrame.Height);
        }

        SubmitDockHandle(_currentDockVisualFrame, _dockVisualOpacity);
    }

    private double CurrentDockVisualScale() =>
        DockVisual.RenderTransform is ScaleTransform transform
            ? transform.ScaleX
            : 1;

    internal static bool ShouldAnchorDockAnimationToPointer(
        DockState state,
        bool titleDragging,
        bool dockDragging,
        bool compactToCompact) =>
        !compactToCompact
        && (titleDragging || dockDragging)
        && state == DockState.RestoreDrag;

    private void MoveExpandedWindow(WpfPoint cursor)
    {
        MonitorSnapshot monitor = MonitorForPoint(cursor);
        WpfPoint position = new(cursor.X - _pointerGrabOffset.X, cursor.Y - _pointerGrabOffset.Y);
        CommitPhysicalBounds(position, CurrentWindowWidth(), CurrentWindowHeight(), monitor.Info.Dpi, null, synchronizeWpf: true);
    }

    private void MoveExpandedFromDock(WpfPoint cursor)
    {
        MonitorSnapshot monitor = MonitorForPoint(cursor);
        double width = Math.Max(ExpandedMinWidth, _expandedWidth);
        double height = Math.Max(ExpandedMinHeight, _expandedHeight);
        WpfPoint position = ExpandedPositionForCursor(cursor, width, monitor);
        CommitPhysicalBounds(position, width, height, monitor.Info.Dpi, null, synchronizeWpf: true);
    }

    private WpfPoint ExpandedPositionForCursor(WpfPoint cursor, double width, MonitorSnapshot monitor)
    {
        WpfPoint anchor = new(
            (width / 2 + _titleBarDragCenterOffsetX) * monitor.Info.Dpi.ScaleX,
            (_hasMeasuredTitleBarDragCenter ? _titleBarDragCenterY : ExpandedTitleBarHeight / 2) * monitor.Info.Dpi.ScaleY);
        return new WpfPoint(cursor.X - anchor.X, cursor.Y - anchor.Y);
    }

    private WpfPoint PointerAnchoredAnimationPosition(
        WpfPoint cursor,
        double width,
        DockVisualFrame frame,
        double dockOpacity,
        MonitorSnapshot monitor)
    {
        WpfPoint titleAnchor = new(
            (width / 2 + _titleBarDragCenterOffsetX) * monitor.Info.Dpi.ScaleX,
            (_hasMeasuredTitleBarDragCenter ? _titleBarDragCenterY : ExpandedTitleBarHeight / 2) * monitor.Info.Dpi.ScaleY);
        WpfPoint dockAnchor = new(
            (frame.ContentOffset.X + (_dockSize / 2)) * monitor.Info.Dpi.ScaleX,
            (frame.ContentOffset.Y + (_dockSize / 2)) * monitor.Info.Dpi.ScaleY);
        WpfPoint anchor = DockVisualLayoutCalculator.Lerp(
            titleAnchor,
            dockAnchor,
            Math.Clamp(dockOpacity, 0, 1));
        return new WpfPoint(cursor.X - anchor.X, cursor.Y - anchor.Y);
    }

    private (WpfPoint Position, double Width, double Height) ClampExpandedBounds(
        MonitorSnapshot monitor,
        WpfPoint requestedPosition,
        double requestedWidth,
        double requestedHeight)
    {
        WpfRect area = monitor.Info.WorkingArea;
        DpiScale2 dpi = monitor.Info.Dpi;
        double maxWidth = Math.Max(1, area.Width / dpi.ScaleX);
        double maxHeight = Math.Max(1, area.Height / dpi.ScaleY);
        double width = Math.Clamp(requestedWidth, Math.Min(ExpandedMinWidth, maxWidth), maxWidth);
        double height = Math.Clamp(requestedHeight, Math.Min(ExpandedMinHeight, maxHeight), maxHeight);
        double physicalWidth = Math.Round(width * dpi.ScaleX);
        double physicalHeight = Math.Round(height * dpi.ScaleY);
        double maxX = Math.Max(area.Left, area.Right - physicalWidth);
        double maxY = Math.Max(area.Top, area.Bottom - physicalHeight);
        return (
            new WpfPoint(
                Math.Clamp(requestedPosition.X, area.Left, maxX),
                Math.Clamp(requestedPosition.Y, area.Top, maxY)),
            width,
            height);
    }

    private void SaveCurrentExpandedBounds()
    {
        if (DockState != DockState.Expanded || !IsVisible)
        {
            return;
        }

        WpfRect bounds = CurrentPhysicalBounds();
        DpiScale2 dpi = _dockMonitorService.FromPoint(bounds.TopLeft).Dpi;
        SaveExpandedBounds(bounds.TopLeft, bounds.Width / dpi.ScaleX, bounds.Height / dpi.ScaleY);
    }

    private void SaveExpandedBounds(WpfPoint position, double width, double height)
    {
        _expandedPosition = position;
        _expandedWidth = Math.Max(ExpandedMinWidth, width);
        _expandedHeight = Math.Max(ExpandedMinHeight, height);
        _hasExpandedBounds = true;
    }

    private void ScheduleExpandedBoundsSave()
    {
        if (!_dockingInitialized
            || _isSynchronizingExpandedStartupBounds
            || DockState != DockState.Expanded
            || !IsVisible
            || _titleDragging)
        {
            return;
        }

        _expandedBoundsSaveTimer?.Stop();
        DispatcherTimer timer = new()
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_expandedBoundsSaveTimer, timer))
            {
                return;
            }

            _expandedBoundsSaveTimer = null;
            SaveCurrentExpandedBounds();
            PersistRuntimeWindowState();
        };
        _expandedBoundsSaveTimer = timer;
        timer.Start();
    }

    private void PersistRuntimeWindowState()
    {
        CopyRuntimeWindowStateTo(_settings);
        if (_persistWindowStateAsync is not null)
        {
            _persistWindowStateAsync(_settings.Clone()).Observe();
        }
    }

    private void OnDockWindowLocationChanged(object? sender, EventArgs e) => ScheduleExpandedBoundsSave();
    private void OnDockWindowSizeChanged(object sender, SizeChangedEventArgs e) => ScheduleExpandedBoundsSave();

    private void OnDockWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            _dockHandleController?.Hide();
            HandleInterruptedInteraction(DockStateEvent.Hide);
        }
        else if (DockState == DockState.Docked)
        {
            SubmitDockHandle(_currentDockVisualFrame, _dockVisualOpacity);
        }
    }

    private void OnDockWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is bool visible && !visible)
        {
            _dockHandleController?.Hide();
            HandleInterruptedInteraction(DockStateEvent.Hide);
        }
        else
        {
            _dockStateMachine.Show();
            if (DockState == DockState.Docked)
            {
                SubmitDockHandle(_currentDockVisualFrame, _dockVisualOpacity);
            }
        }
    }

    private void OnDockWindowDeactivated(object? sender, EventArgs e)
    {
        if (_titleDragging
            || _dockDragging
            || _dockRightButtonPending
            || DockState is DockState.DockPreview or DockState.RestoreDrag)
        {
            HandleInterruptedInteraction(DockStateEvent.Deactivated);
        }
    }

    private void OnDockWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (DockState == DockState.Docked)
        {
            CommitPhysicalBounds(
                _animationTargetPosition,
                _animationTargetWidth,
                _animationTargetHeight,
                _dockDpi,
                _animationTargetDockVisualFrame.Outline,
                synchronizeWpf: true);
            SetDockedVisualState();
            return;
        }

        SynchronizeExpandedStartupBounds();
    }

    private void HandleInterruptedInteraction(DockStateEvent interruption)
    {
        StopPointerReleaseWatch();
        DockStateTransition transition = _dockStateMachine.Apply(interruption);
        bool animationWasRunning = _dockAnimation is not null;
        _titleDragging = false;
        _dockDragging = false;
        _dockDragStarted = false;
        _dockRightButtonPending = false;
        ReleasePointerCapture();
        _dockAnimation?.Cancel();
        _dockAnimation = null;
        if (transition.FinalizeDock)
        {
            _finalizeDockWhenAnimationCompletes = true;
            FinalizeDock();
        }
        else if (transition.RestoreExpanded)
        {
            RestoreExpandedImmediately();
        }
        else if (DockState == DockState.Docked && animationWasRunning)
        {
            FinalizeDock();
        }
        else
        {
            PersistRuntimeWindowState();
        }
    }

    private void UpdateRestoreDragTarget(WpfPoint cursor)
    {
        MonitorSnapshot monitor = MonitorForPoint(cursor);
        _dockDpi = monitor.Info.Dpi;
        (WpfPoint position, double width, double height) target = ExpandedBoundsForCursor(cursor, monitor);
        _animationTargetPosition = target.position;
        _animationTargetWidth = target.width;
        _animationTargetHeight = target.height;
        if (_dockAnimation is null)
        {
            CommitPhysicalBounds(
                target.position,
                target.width,
                target.height,
                monitor.Info.Dpi,
                null,
                synchronizeWpf: true);
        }
    }

    private void StartPointerReleaseWatch()
    {
        StopPointerReleaseWatch();
        DispatcherTimer timer = new()
        {
            Interval = TimeSpan.FromMilliseconds(20)
        };
        timer.Tick += OnPointerReleaseWatchTick;
        _pointerReleaseTimer = timer;
        timer.Start();
    }

    private void OnPointerReleaseWatchTick(object? sender, EventArgs e)
    {
        if (!_titleDragging && !_dockDragging)
        {
            StopPointerReleaseWatch();
            return;
        }

        if (_dockMonitorService.TryGetCursorState(out WpfPoint pointer, out bool leftButtonPressed))
        {
            ApplyPolledPointerState(pointer, leftButtonPressed);
        }
    }

    private void ApplyPolledPointerState(WpfPoint pointer, bool leftButtonPressed)
    {
        if (!_titleDragging && !_dockDragging)
        {
            return;
        }

        if (!leftButtonPressed)
        {
            _latestPointerScreen = pointer;
            CompletePointerInteraction();
            return;
        }

        if (pointer == _latestPointerScreen)
        {
            return;
        }

        if (_dockDragging)
        {
            ProcessDockPointerMove(pointer, leftButtonPressed: true);
        }
        else
        {
            ProcessTitlePointerMove(pointer, leftButtonPressed: true);
        }
    }

    private void StopPointerReleaseWatch()
    {
        DispatcherTimer? timer = _pointerReleaseTimer;
        _pointerReleaseTimer = null;
        if (timer is null)
        {
            return;
        }

        timer.Stop();
        timer.Tick -= OnPointerReleaseWatchTick;
    }

    private void ReleasePointerCapture()
    {
        if (ReferenceEquals(Mouse.Captured, WindowRoot))
        {
            WindowRoot.ReleaseMouseCapture();
        }

        Mouse.Capture(null);
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != nint.Zero && GetCapture() == hwnd)
        {
            _ = ReleaseCapture();
        }

        _dockHandleController?.ReleaseMouseCapture();
    }

    private (WpfPoint Position, double Width, double Height) ExpandedBoundsForCursor(
        WpfPoint cursor,
        MonitorSnapshot monitor)
    {
        double width = Math.Max(ExpandedMinWidth, _expandedWidth);
        double height = Math.Max(ExpandedMinHeight, _expandedHeight);
        return (ExpandedPositionForCursor(cursor, width, monitor), width, height);
    }

    private void PrepareExpandedSurfaceForAnimation()
    {
        ScaleTransform transform = ExpandedSurface.RenderTransform as ScaleTransform
            ?? new ScaleTransform(1, 1);
        if (!ReferenceEquals(ExpandedSurface.RenderTransform, transform))
        {
            ExpandedSurface.RenderTransformOrigin = new WpfPoint(0.5, 0.5);
            ExpandedSurface.RenderTransform = transform;
        }

        double oldBaseWidth = double.IsNaN(ExpandedSurface.Width)
            ? _animationFromWidth
            : ExpandedSurface.Width;
        double oldBaseHeight = double.IsNaN(ExpandedSurface.Height)
            ? _animationFromHeight
            : ExpandedSurface.Height;
        double effectiveWidth = oldBaseWidth * transform.ScaleX;
        double effectiveHeight = oldBaseHeight * transform.ScaleY;
        double baseWidth = Math.Max(oldBaseWidth, _animationTargetWidth);
        double baseHeight = Math.Max(oldBaseHeight, _animationTargetHeight);

        ExpandedSurface.Width = baseWidth;
        ExpandedSurface.Height = baseHeight;
        ExpandedSurface.HorizontalAlignment = _dockEdge switch
        {
            MainWindowDockEdge.Left => System.Windows.HorizontalAlignment.Left,
            MainWindowDockEdge.Right => System.Windows.HorizontalAlignment.Right,
            _ => System.Windows.HorizontalAlignment.Center
        };
        ExpandedSurface.VerticalAlignment = _dockEdge switch
        {
            MainWindowDockEdge.Top => System.Windows.VerticalAlignment.Top,
            MainWindowDockEdge.Bottom => System.Windows.VerticalAlignment.Bottom,
            _ => System.Windows.VerticalAlignment.Center
        };
        ExpandedSurface.RenderTransformOrigin = _dockEdge switch
        {
            MainWindowDockEdge.Left => new WpfPoint(0, 0.5),
            MainWindowDockEdge.Right => new WpfPoint(1, 0.5),
            MainWindowDockEdge.Top => new WpfPoint(0.5, 0),
            MainWindowDockEdge.Bottom => new WpfPoint(0.5, 1),
            _ => new WpfPoint(0.5, 0.5)
        };

        _animationFromSurfaceScaleX = effectiveWidth / Math.Max(1, baseWidth);
        _animationFromSurfaceScaleY = effectiveHeight / Math.Max(1, baseHeight);
        _animationTargetSurfaceScaleX = _animationTargetExpandedOpacity >= 0.5
            ? 1
            : _animationTargetWidth / Math.Max(1, baseWidth);
        _animationTargetSurfaceScaleY = _animationTargetExpandedOpacity >= 0.5
            ? 1
            : _animationTargetHeight / Math.Max(1, baseHeight);
        SetExpandedSurfaceScale(_animationFromSurfaceScaleX, _animationFromSurfaceScaleY);
        ExpandedSurface.IsHitTestVisible = false;
        DockLayer.IsHitTestVisible = _animationTargetDockOpacity >= 0.999;
    }

    private void SetExpandedSurfaceScale(double x, double y)
    {
        if (ExpandedSurface.RenderTransform is ScaleTransform transform)
        {
            transform.ScaleX = x;
            transform.ScaleY = y;
        }
    }

    internal void DisposeDockingInteraction()
    {
        if (!_dockingInitialized)
        {
            return;
        }

        _dockingInitialized = false;
        _dockAnimation?.Dispose();
        _dockAnimation = null;
        _dockScaleAnimation?.Dispose();
        _dockScaleAnimation = null;
        _restoreCompletion = null;
        _expandedBoundsSaveTimer?.Stop();
        _expandedBoundsSaveTimer = null;
        StopPointerReleaseWatch();
        ReleasePointerCapture();
        ThemePreferences.PaletteChanged -= OnDockPaletteChanged;
        _dockHandleController?.Dispose();
        _dockHandleController = null;
        _dockWindowAdapter?.Dispose();
        _dockWindowAdapter = null;
        WindowRoot.MouseMove -= OnPointerMouseMove;
        WindowRoot.MouseLeftButtonUp -= OnPointerMouseLeftButtonUp;
        WindowRoot.LostMouseCapture -= OnPointerLostMouseCapture;
        LocationChanged -= OnDockWindowLocationChanged;
        SizeChanged -= OnDockWindowSizeChanged;
        StateChanged -= OnDockWindowStateChanged;
        Deactivated -= OnDockWindowDeactivated;
        IsVisibleChanged -= OnDockWindowVisibilityChanged;
        Loaded -= OnDockWindowLoaded;
    }

    internal void PrepareDockingForClose()
    {
        _dockStateMachine.Apply(DockStateEvent.Close);
        _dockAnimation?.Cancel();
        _dockAnimation = null;
        _dockScaleAnimation?.Cancel();
        _dockScaleAnimation = null;
        _restoreCompletion = null;
    }

    private bool TryFindDockEdge(WpfPoint point, out MainWindowDockEdge edge, out MonitorSnapshot monitor)
    {
        monitor = MonitorForPoint(point);
        edge = MainWindowDockEdge.Left;
        if (!_dockStateMachine.Enabled)
        {
            return false;
        }

        WpfRect area = monitor.Info.WorkingArea;
        (MainWindowDockEdge Edge, double Distance)[] candidates =
        [
            (MainWindowDockEdge.Left, Math.Abs(point.X - area.Left)),
            (MainWindowDockEdge.Right, Math.Abs(area.Right - point.X)),
            (MainWindowDockEdge.Top, Math.Abs(point.Y - area.Top)),
            (MainWindowDockEdge.Bottom, Math.Abs(area.Bottom - point.Y))
        ];
        (edge, double distance) = candidates.OrderBy(candidate => candidate.Distance).First();
        return distance <= DockDetectionPixels;
    }

    private static bool IsPastDetachThreshold(WpfPoint point, MainWindowDockEdge edge, WpfRect area)
    {
        double inward = edge switch
        {
            MainWindowDockEdge.Left => point.X - area.Left,
            MainWindowDockEdge.Right => area.Right - point.X,
            MainWindowDockEdge.Top => point.Y - area.Top,
            MainWindowDockEdge.Bottom => area.Bottom - point.Y,
            _ => 0
        };
        return inward > DockDetachPixels;
    }

    internal static bool HasReachedDockDragThreshold(WpfPoint press, WpfPoint current, double scaling)
    {
        double dx = press.X - current.X;
        double dy = press.Y - current.Y;
        return Math.Sqrt(dx * dx + dy * dy) >= DockDragThresholdDips * Math.Max(0.01, scaling);
    }

    private WpfPoint CursorScreenPosition(MouseEventArgs e)
    {
        if (GetCursorPos(out NativePoint point))
        {
            return new WpfPoint(point.X, point.Y);
        }

        WpfPoint local = e.GetPosition(this);
        WpfPoint origin = PointToScreen(new WpfPoint(0, 0));
        DpiScale2 dpi = DpiCoordinateModel.FromVisual(this);
        return new WpfPoint(origin.X + local.X * dpi.ScaleX, origin.Y + local.Y * dpi.ScaleY);
    }

    private WpfRect CurrentPhysicalBounds()
    {
        if (_dockWindowAdapter?.TryGetLogicalBounds(out WpfRect bounds) == true)
        {
            return bounds;
        }

        DpiScale2 dpi = DpiCoordinateModel.FromVisual(this);
        return new WpfRect(
            Math.Round(Left * dpi.ScaleX),
            Math.Round(Top * dpi.ScaleY),
            Math.Max(1, Math.Round(Math.Max(1, Width) * dpi.ScaleX)),
            Math.Max(1, Math.Round(Math.Max(1, Height) * dpi.ScaleY)));
    }

    private void CommitPhysicalBounds(
        WpfPoint position,
        double widthDip,
        double heightDip,
        DpiScale2 dpi,
        DockOutline? outline,
        bool synchronizeWpf)
    {
        DockOutline? regionOutline = DockRenderBackend == DockHandleRenderBackend.RegionFallback
            ? outline
            : null;
        if (_dockWindowAdapter?.Commit(position, widthDip, heightDip, dpi, regionOutline, synchronizeWpf) == true)
        {
            return;
        }

        if (synchronizeWpf)
        {
            SetExpandedBoundsDip(position, widthDip, heightDip, dpi);
        }
    }

    private void SetExpandedBoundsDip(WpfPoint position, double widthDip, double heightDip, DpiScale2 dpi)
    {
        Left = position.X / Math.Max(0.01, dpi.ScaleX);
        Top = position.Y / Math.Max(0.01, dpi.ScaleY);
        Width = widthDip;
        Height = heightDip;
    }

    private void SetWindowPositionDip(WpfPoint position, DpiScale2 dpi)
    {
        Left = position.X / Math.Max(0.01, dpi.ScaleX);
        Top = position.Y / Math.Max(0.01, dpi.ScaleY);
    }

    private double CurrentWindowWidth() => double.IsFinite(Width) && Width > 0 ? Width : Math.Max(1, ActualWidth);
    private double CurrentWindowHeight() => double.IsFinite(Height) && Height > 0 ? Height : Math.Max(1, ActualHeight);

    private MonitorSnapshot MonitorForPoint(WpfPoint point)
    {
        PixelMonitorInfo info = _dockMonitorService.FromPoint(point);
        return new MonitorSnapshot(info, info.WorkingArea == _dockMonitorService.Primary.Info.WorkingArea);
    }

    private void MeasureTitleBarDragCenter()
    {
        if (TitleBar.ActualWidth <= 0 || TitleBar.ActualHeight <= 0)
        {
            return;
        }

        WpfPoint center = TitleBar.TranslatePoint(
            new WpfPoint(TitleBar.ActualWidth / 2, TitleBar.ActualHeight / 2),
            this);
        _titleBarDragCenterOffsetX = center.X - (Width / 2);
        _titleBarDragCenterY = center.Y;
        _hasMeasuredTitleBarDragCenter = true;
    }

    private static bool NativeBoundsAreClose(
        WpfRect actual,
        WpfPoint position,
        double widthDip,
        double heightDip,
        DpiScale2 dpi)
    {
        WpfRect target = new(
            Math.Round(position.X),
            Math.Round(position.Y),
            Math.Max(1, Math.Round(widthDip * dpi.ScaleX)),
            Math.Max(1, Math.Round(heightDip * dpi.ScaleY)));
        return Math.Abs(actual.Left - target.Left) < 1
            && Math.Abs(actual.Top - target.Top) < 1
            && Math.Abs(actual.Width - target.Width) < 1
            && Math.Abs(actual.Height - target.Height) < 1;
    }

    private static double Lerp(double from, double to, double amount) => from + ((to - from) * amount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint GetCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        internal readonly int X;
        internal readonly int Y;

        internal NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }
}

internal static class PixelMonitorInfoExtensions
{
    internal static MonitorSnapshot ToSnapshot(this PixelMonitorInfo info) => new(info, true);
}

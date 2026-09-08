using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using MemeMomo.Services;
using MemeMomo.UI;
using MemeMomo.UI.Windows;

namespace MemeMomo.Views;

using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;
using WpfButton = System.Windows.Controls.Button;
using WpfPath = System.Windows.Shapes.Path;

/// <summary>Custom non-activating tray/dock flyout. NotifyIcon integration is supplied by plan 11.</summary>
public partial class TrayMenuWindow : BorderlessWindow
{
    private readonly ITrayMenuHostActions? _actions;
    private HwndSource? _hwndSource;
    private bool _inputMonitoring;
    private bool _hasBeenShown;

    public TrayMenuWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    public TrayMenuWindow(ITrayMenuHostActions actions)
        : this()
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        UpdatePinStatus();
    }

    public TrayMenuWindow(MainWindow mainWindow, Action exitApplication)
        : this(new MainWindowTrayActions(mainWindow, exitApplication))
    {
    }

    public bool IsMenuVisible => IsVisible;
    internal WpfButton OpenButtonPart => OpenButton;
    internal WpfButton NewButtonPart => NewButton;
    internal WpfButton PinButtonPart => PinButton;
    internal WpfButton ExitButtonPart => ExitButton;
    internal WpfPath PinIconPart => PinIconPath;
    internal Rect LastPlacementPixels { get; private set; }
    internal void CloseImmediatelyForTest() => CloseImmediately();

    public void ShowNearPointer(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        MonitorService monitorService = new();
        if (monitorService.TryGetCursorState(out WpfPoint cursor, out _))
        {
            ShowNearPointer(owner, cursor);
            return;
        }

        PixelMonitorInfo screen = monitorService.FromWindow(owner);
        Rect area = screen.WorkingArea;
        WpfPoint fallback = new(area.Right - 4, area.Bottom - 4);
        ShowNearPointer(owner, fallback);
    }

    public void ShowNearPointer(Window owner, WpfPoint cursorPixels)
    {
        ArgumentNullException.ThrowIfNull(owner);
        PixelMonitorInfo monitor = new MonitorService().FromPoint(cursorPixels);
        WpfSize menuSize = MeasureMenuSize();
        Width = menuSize.Width;
        Rect placementPixels = CalculatePlacement(
            monitor.WorkingArea,
            cursorPixels,
            menuSize,
            monitor.Dpi,
            4);
        LastPlacementPixels = placementPixels;
        DpiScale2 dpi = monitor.Dpi;
        Left = placementPixels.Left / dpi.ScaleX;
        Top = placementPixels.Top / dpi.ScaleY;
        Topmost = true;
        UpdatePinStatus();
        if (!IsVisible)
        {
            bool firstShow = !_hasBeenShown;
            if (!firstShow)
            {
                PrepareForOpen();
            }

            Show();
            _hasBeenShown = true;
            if (!firstShow)
            {
                PlayOpenTransition();
            }
        }
        else
        {
            PrepareForOpen();
            PlayOpenTransition();
        }
        StartInputMonitoring();
        PlaceWithoutActivation(placementPixels);

        // Keep the flyout above a shell-owned tray popup without activating it.
        Dispatcher.BeginInvoke(
            new Action(() => PlaceWithoutActivation(placementPixels)),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private WpfSize MeasureMenuSize()
    {
        MenuLayout.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));
        double width = Math.Ceiling(MenuLayout.DesiredSize.Width + 2);
        return new WpfSize(Math.Max(1, width), Height);
    }

    internal static Rect CalculatePlacement(
        Rect workingAreaPixels,
        WpfPoint cursorPixels,
        WpfSize menuDip,
        DpiScale2 dpi,
        double marginDip)
    {
        double width = Math.Max(1, menuDip.Width * dpi.ScaleX);
        double height = Math.Max(1, menuDip.Height * dpi.ScaleY);
        double marginX = Math.Max(0, marginDip * dpi.ScaleX);
        double marginY = Math.Max(0, marginDip * dpi.ScaleY);
        double minX = workingAreaPixels.Left + marginX;
        double maxX = workingAreaPixels.Right - width - marginX;
        double minY = workingAreaPixels.Top + marginY;
        double maxY = workingAreaPixels.Bottom - height - marginY;
        if (maxX < minX) maxX = minX;
        if (maxY < minY) maxY = minY;

        // Prefer the upper-right quadrant. If either side has no room, flip
        // across the cursor before applying the final work-area clamp.
        double x = cursorPixels.X + marginX;
        if (x + width > workingAreaPixels.Right - marginX)
        {
            x = cursorPixels.X - width - marginX;
        }

        double y = cursorPixels.Y - height - marginY;
        if (y < workingAreaPixels.Top + marginY)
        {
            y = cursorPixels.Y + marginY;
        }

        return new Rect(Math.Clamp(x, minX, maxX), Math.Clamp(y, minY, maxY), width, height);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        WpfPoint position = Mouse.GetPosition(this);
        if (Mouse.LeftButton == MouseButtonState.Pressed && IsPointInsideMenu(position))
        {
            return;
        }

        HideMenu();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        ExecuteMenuCommand(_actions is null ? null : _actions.OpenMainWindow);
    }

    private void OnNewMemoClick(object sender, RoutedEventArgs e)
    {
        ExecuteMenuCommand(_actions is null ? null : _actions.CreateNewMemo);
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        ExecuteMenuCommand(_actions is null ? null : _actions.ToggleMainWindowPinned);
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        ExecuteMenuCommand(_actions is null ? null : _actions.ExitApplication);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(OnWindowMessage);
    }

    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (!IsVisible || e.StagingItem.Input is not MouseButtonEventArgs mouse
            || mouse.RoutedEvent != Mouse.PreviewMouseDownEvent)
        {
            return;
        }

        WpfPoint position = mouse.GetPosition(this);
        if (!IsPointInsideMenu(position))
        {
            HideMenu();
        }
    }

    private bool IsPointInsideMenu(WpfPoint position) =>
        position.X >= 0
        && position.Y >= 0
        && position.X < ActualWidth
        && position.Y < ActualHeight;

    private bool IsOwnedVisual(DependencyObject source) => ReferenceEquals(Window.GetWindow(source), this);

    internal bool IsOwnedVisualForTest(DependencyObject source) => IsOwnedVisual(source);

    private IntPtr OnWindowMessage(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        const int WmActivateApp = 0x001C;
        if (message == WmActivateApp && wParam == IntPtr.Zero)
        {
            Dispatcher.BeginInvoke(HideMenu);
        }

        return IntPtr.Zero;
    }

    private void StartInputMonitoring()
    {
        if (_inputMonitoring)
        {
            return;
        }

        InputManager.Current.PreProcessInput += OnPreProcessInput;
        _inputMonitoring = true;
    }

    private void StopInputMonitoring()
    {
        if (!_inputMonitoring)
        {
            return;
        }

        InputManager.Current.PreProcessInput -= OnPreProcessInput;
        _inputMonitoring = false;
    }

    private void HideMenu()
    {
        StopInputMonitoring();
        if (IsVisible)
        {
            HideWithTransition();
        }
    }

    private void ExecuteMenuCommand(Action? command)
    {
        HideMenu();
        if (command is not null)
        {
            Dispatcher.BeginInvoke(command, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopInputMonitoring();
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(OnWindowMessage);
            _hwndSource = null;
        }

        SourceInitialized -= OnSourceInitialized;
        Closed -= OnClosed;
    }

    private void UpdatePinStatus()
    {
        bool pinned = _actions?.IsMainWindowPinned == true;
        InteractionState.SetIsPinActive(PinButton, pinned);
        if (PinIconPath.RenderTransform is RotateTransform rotation)
        {
            rotation.Angle = pinned ? -45 : 0;
        }
        MemeMomo.UI.Text.LocalizeExtension.Set(PinButton, AutomationProperties.NameProperty, pinned ? "窗口置顶，已开启" : "窗口置顶，已关闭");
    }

    private void PlaceWithoutActivation(Rect placementPixels)
    {
        WindowInteropHelper helper = new(this);
        if (helper.Handle == nint.Zero) return;
        SetWindowPos(
            helper.Handle,
            HwndTopmost,
            (int)Math.Round(placementPixels.Left),
            (int)Math.Round(placementPixels.Top),
            (int)Math.Round(placementPixels.Width),
            (int)Math.Round(placementPixels.Height),
            SwpNoActivate | SwpShowWindow);
    }

    private sealed class MainWindowTrayActions(MainWindow mainWindow, Action exitApplication) : ITrayMenuHostActions
    {
        public bool IsMainWindowPinned => mainWindow.Topmost;
        public void OpenMainWindow() => mainWindow.ShowExpandedWithTransition(focusInput: false);
        public void CreateNewMemo() => mainWindow.ShowExpandedWithTransition(focusInput: true);
        public void ToggleMainWindowPinned() => mainWindow.TogglePinned();
        public void ExitApplication() => exitApplication();
    }

    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

}

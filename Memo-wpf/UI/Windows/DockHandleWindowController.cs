using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfPoint = System.Windows.Point;
using WpfBrush = System.Windows.Media.Brush;

namespace Memo.UI.Windows;

internal delegate bool DockHandleWindowFactory(
    nint owner,
    IDockHandleWindowEvents events,
    out IDockHandleNativeWindow? window,
    out Exception? error);

internal enum DockHandleRenderBackend
{
    Layered,
    RegionFallback,
    Unavailable
}

internal interface IDockHandleInputSink
{
    void OnDockHandleLeftButtonDown(WpfPoint screenPoint);
    void OnDockHandleMouseMove(WpfPoint screenPoint, bool leftButtonPressed);
    void OnDockHandleLeftButtonUp(WpfPoint screenPoint);
    void OnDockHandleRightButtonDown(WpfPoint screenPoint);
    void OnDockHandleRightButtonUp(WpfPoint screenPoint);
    void OnDockHandleCaptureLost();
    void OnDockHandleEnvironmentChanged();
}

internal readonly record struct DockHandlePresentation(
    WpfPoint PositionPixels,
    DockVisualFrame Frame,
    double DockSize,
    double Scale,
    double Opacity,
    DpiScale2 Dpi,
    WpfBrush Fill,
    WpfBrush Stroke,
    bool Topmost);

/// <summary>Coordinates renderer, native HWND, failure latching, and input forwarding.</summary>
internal sealed class DockHandleWindowController : IDockHandleWindowEvents, IDisposable
{
    private const int PermanentFailureThreshold = 3;

    private readonly Window _owner;
    private readonly IDockHandleInputSink _input;
    private readonly IDockHandleRenderer _renderer;
    private readonly DockHandleWindowFactory _windowFactory;
    private readonly ImageSource? _icon;
    private IDockHandleNativeWindow? _window;
    private DockHandlePresentation? _pending;
    private DockHandleRenderRequest? _lastRequest;
    private bool _fallbackForSession;
    private bool _permanentlyDisabled;
    private int _consecutiveFailures;
    private int _disposed;

    internal DockHandleWindowController(
        Window owner,
        IDockHandleInputSink input,
        IDockHandleRenderer? renderer = null,
        ImageSource? icon = null,
        DockHandleWindowFactory? windowFactory = null)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _renderer = renderer ?? new DockHandleRenderer();
        _windowFactory = windowFactory ?? CreateWindow;
        _icon = icon ?? TryLoadIcon();
        _owner.SourceInitialized += OnOwnerSourceInitialized;
        _owner.Closed += OnOwnerClosed;
    }

    internal DockHandleRenderBackend Backend => Volatile.Read(ref _disposed) != 0
        ? DockHandleRenderBackend.Unavailable
        : _fallbackForSession || _permanentlyDisabled
            ? DockHandleRenderBackend.RegionFallback
            : DockHandleRenderBackend.Layered;

    internal string? LastFailure { get; private set; }

    internal bool IsVisible => _window?.IsVisible == true;

    internal void BeginSession()
    {
        if (_permanentlyDisabled || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (_fallbackForSession)
        {
            return;
        }

        LastFailure = null;
        _ = EnsureWindow();
    }

    internal bool Submit(DockHandlePresentation presentation)
    {
        if (Backend != DockHandleRenderBackend.Layered)
        {
            return false;
        }

        if (!_owner.Dispatcher.CheckAccess())
        {
            _owner.Dispatcher.BeginInvoke(() => Submit(presentation));
            return true;
        }

        _pending = presentation;
        nint ownerHandle = new WindowInteropHelper(_owner).Handle;
        if (ownerHandle == nint.Zero)
        {
            return true;
        }

        if (!EnsureWindow())
        {
            return false;
        }

        try
        {
            DockHandleRenderRequest request = new(
                presentation.Frame,
                presentation.DockSize,
                presentation.Scale,
                presentation.Opacity,
                presentation.Dpi,
                presentation.Fill,
                presentation.Stroke,
                _icon);
            DockHandleBitmap bitmap = _renderer.Render(request);
            int error = 0;
            if (_window is null
                || !_window.TryUpdate(bitmap, presentation.PositionPixels, out error))
            {
                ActivateFallback($"UpdateLayeredWindow failed with Win32 error {error}.");
                return false;
            }

            _lastRequest = request;
            _window.SetTopmost(presentation.Topmost);
            if (presentation.Opacity > 0.001 && _owner.IsVisible)
            {
                _window.Show();
            }
            else
            {
                _window.Hide();
            }

            _consecutiveFailures = 0;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException or OutOfMemoryException)
        {
            ActivateFallback($"Layered dock rendering failed: {ex.Message}");
            return false;
        }
    }

    internal void Hide(bool resetSession = false)
    {
        _window?.Hide();
        _lastRequest = null;
        if (resetSession && !_permanentlyDisabled)
        {
            _fallbackForSession = false;
            LastFailure = null;
        }
    }

    internal void ReleaseMouseCapture() => _window?.ReleaseMouseCapture();

    internal void SetTopmost(bool topmost) => _window?.SetTopmost(topmost);

    internal void ActivateFallback(string reason)
    {
        if (_fallbackForSession || _permanentlyDisabled)
        {
            return;
        }

        _fallbackForSession = true;
        _consecutiveFailures++;
        _permanentlyDisabled = _consecutiveFailures >= PermanentFailureThreshold;
        LastFailure = reason;
        _window?.Hide();
        _lastRequest = null;
        _window?.Dispose();
        _window = null;
        Trace.TraceError($"Dock handle switched to RegionFallback: {reason}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _owner.SourceInitialized -= OnOwnerSourceInitialized;
        _owner.Closed -= OnOwnerClosed;
        _window?.ReleaseMouseCapture();
        _window?.Dispose();
        _window = null;
        _pending = null;
        _lastRequest = null;
    }

    bool IDockHandleWindowEvents.HitTest(int xPixels, int yPixels) =>
        _lastRequest is DockHandleRenderRequest request
        && DockHandleRenderer.HitTest(request, xPixels, yPixels);

    void IDockHandleWindowEvents.LeftButtonDown(WpfPoint screenPoint) =>
        _input.OnDockHandleLeftButtonDown(screenPoint);

    void IDockHandleWindowEvents.MouseMove(WpfPoint screenPoint, bool leftButtonPressed) =>
        _input.OnDockHandleMouseMove(screenPoint, leftButtonPressed);

    void IDockHandleWindowEvents.LeftButtonUp(WpfPoint screenPoint) =>
        _input.OnDockHandleLeftButtonUp(screenPoint);

    void IDockHandleWindowEvents.RightButtonDown(WpfPoint screenPoint) =>
        _input.OnDockHandleRightButtonDown(screenPoint);

    void IDockHandleWindowEvents.RightButtonUp(WpfPoint screenPoint) =>
        _input.OnDockHandleRightButtonUp(screenPoint);

    void IDockHandleWindowEvents.CaptureLost() => _input.OnDockHandleCaptureLost();

    void IDockHandleWindowEvents.EnvironmentChanged() => _input.OnDockHandleEnvironmentChanged();

    private bool EnsureWindow()
    {
        if (_window is not null)
        {
            return true;
        }

        nint ownerHandle = new WindowInteropHelper(_owner).Handle;
        if (ownerHandle == nint.Zero)
        {
            return true;
        }

        if (!_windowFactory(ownerHandle, this, out _window, out Exception? error))
        {
            ActivateFallback($"Layered dock window initialization failed: {error?.Message ?? "unknown error"}");
            return false;
        }

        return true;
    }

    private void OnOwnerSourceInitialized(object? sender, EventArgs e)
    {
        if (Backend == DockHandleRenderBackend.Layered && EnsureWindow() && _pending.HasValue)
        {
            _ = Submit(_pending.Value);
        }
    }

    private void OnOwnerClosed(object? sender, EventArgs e) => Dispose();

    private static bool CreateWindow(
        nint owner,
        IDockHandleWindowEvents events,
        out IDockHandleNativeWindow? window,
        out Exception? error)
    {
        bool created = DockHandleWindow.TryCreate(owner, events, out DockHandleWindow? concrete, out error);
        window = concrete;
        return created;
    }

    private static ImageSource? TryLoadIcon()
    {
        try
        {
            BitmapImage icon = new();
            icon.BeginInit();
            icon.CacheOption = BitmapCacheOption.OnLoad;
            icon.UriSource = new Uri("pack://application:,,,/Memo;component/Assets/appicon.png", UriKind.Absolute);
            icon.EndInit();
            icon.Freeze();
            return icon;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            Trace.TraceWarning($"Dock handle icon could not be loaded: {ex.Message}");
            return null;
        }
    }
}

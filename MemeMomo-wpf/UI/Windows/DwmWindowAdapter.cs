using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MemeMomo.UI.Windows;

/// <summary>
/// Centralizes the native corner/region fallback. The default path is DWM on
/// Windows 11; Windows 10 and older DWM builds receive a DPI-scaled rounded
/// region without changing the WPF layout coordinate model.
/// </summary>
public sealed class DwmWindowAdapter : IDisposable
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowBorderColor = 34;
    private const int DwmColorDefault = -1;
    private const int DwmColorNone = -2;
    private const int DwmDoNotRound = 1;
    private const int DwmRound = 2;
    private readonly Window _window;
    private readonly double _cornerRadius;
    private nint _hwnd;
    private bool _customRegionActive;
    private int _disposed;

    public DwmWindowAdapter(Window window, double cornerRadius = 14)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _cornerRadius = Math.Max(0, cornerRadius);
        _window.SourceInitialized += OnSourceInitialized;
        _window.SizeChanged += OnSizeChanged;
        _window.Closed += OnClosed;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _window.SourceInitialized -= OnSourceInitialized;
        _window.SizeChanged -= OnSizeChanged;
        _window.Closed -= OnClosed;
        _hwnd = nint.Zero;
    }

    internal void SetCustomRegionActive(bool active)
    {
        _customRegionActive = active;
        ApplyDwmCornerPreference(active);
        ApplyDockBorderState(active);
        if (!active)
        {
            ApplyWin10Region();
        }
    }

    private void ApplyDwmCornerPreference(bool customRegionActive)
    {
        if (_hwnd == nint.Zero || Environment.OSVersion.Version.Build < 22000)
        {
            return;
        }

        int preference = customRegionActive ? DwmDoNotRound : DwmRound;
        _ = DwmSetWindowAttribute(_hwnd, DwmWindowCornerPreference, ref preference, sizeof(int));
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(_window).Handle;
        if (_hwnd == nint.Zero)
        {
            return;
        }

        int preference = _customRegionActive ? DwmDoNotRound : DwmRound;
        _ = DwmSetWindowAttribute(_hwnd, DwmWindowCornerPreference, ref preference, sizeof(int));
        ApplyDockBorderState(_customRegionActive);
        ApplyWin10Region();
    }

    private void ApplyDockBorderState(bool docked)
    {
        if (_hwnd == nint.Zero || Environment.OSVersion.Version.Build < 22000)
        {
            return;
        }

        int color = docked ? DwmColorNone : DwmColorDefault;
        _ = DwmSetWindowAttribute(_hwnd, DwmWindowBorderColor, ref color, sizeof(int));
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_customRegionActive)
        {
            ApplyWin10Region();
        }
    }

    private void ApplyWin10Region()
    {
        if (_customRegionActive || _hwnd == nint.Zero || Environment.OSVersion.Version.Build >= 22000)
        {
            return;
        }

        DpiScale2 dpi = _window is Visual visual ? DpiCoordinateModel.FromVisual(visual) : DpiScale2.Default;
        int width = Math.Max(1, (int)Math.Round(_window.ActualWidth * dpi.ScaleX));
        int height = Math.Max(1, (int)Math.Round(_window.ActualHeight * dpi.ScaleY));
        int radius = Math.Max(1, (int)Math.Round(_cornerRadius * Math.Max(dpi.ScaleX, dpi.ScaleY)));
        nint region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (region != nint.Zero)
        {
            _ = SetWindowRgn(_hwnd, region, true);
        }
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int valueSize);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
}

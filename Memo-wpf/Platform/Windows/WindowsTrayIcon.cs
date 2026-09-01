using System.Drawing;
using System.IO;
using System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace Memo.Platform.Windows;

/// <summary>NotifyIcon adapter with the source single/double-click semantics.</summary>
public sealed class WindowsTrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Action _showMenu;
    private readonly Action _showWindow;
    private bool _traySingleClickToShow;
    private long _lastShowTick;
    private int _disposed;

    public WindowsTrayIcon(Action showMenu, Action showWindow)
    {
        _showMenu = showMenu ?? throw new ArgumentNullException(nameof(showMenu));
        _showWindow = showWindow ?? throw new ArgumentNullException(nameof(showWindow));
        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "备忘录",
            Visible = true
        };
        _notifyIcon.MouseUp += OnMouseUp;
        _notifyIcon.MouseClick += OnMouseClick;
        _notifyIcon.MouseDoubleClick += OnMouseDoubleClick;
    }

    public bool TraySingleClickToShow
    {
        get => _traySingleClickToShow;
        set => _traySingleClickToShow = value;
    }

    internal bool IsVisible => _notifyIcon.Visible;

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) _showMenu();
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (_traySingleClickToShow && e.Button == MouseButtons.Left) ShowWindowOncePerClickSequence();
    }

    private void OnMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (!_traySingleClickToShow && e.Button == MouseButtons.Left) ShowWindowOncePerClickSequence();
    }

    private void ShowWindowOncePerClickSequence()
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Exchange(ref _lastShowTick, now);
        if (previous != 0 && now - previous <= SystemInformation.DoubleClickTime)
        {
            return;
        }

        _showWindow();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _notifyIcon.MouseUp -= OnMouseUp;
        _notifyIcon.MouseClick -= OnMouseClick;
        _notifyIcon.MouseDoubleClick -= OnMouseDoubleClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
    }

    private static Icon LoadIcon()
    {
        try
        {
            Uri uri = new("pack://application:,,,/Memo;component/Assets/appicon.ico", UriKind.Absolute);
            System.Windows.Resources.StreamResourceInfo? resource = WpfApplication.GetResourceStream(uri);
            if (resource?.Stream is not null)
            {
                using Stream stream = resource.Stream;
                using MemoryStream copy = new();
                stream.CopyTo(copy);
                copy.Position = 0;
                using Icon loaded = new(copy);
                return (Icon)loaded.Clone();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[Tray] embedded icon load failed: {exception.Message}");
        }

        try
        {
            if (Environment.ProcessPath is string processPath && Icon.ExtractAssociatedIcon(processPath) is Icon icon)
            {
                return icon;
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[Tray] process icon load failed: {exception.Message}");
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}

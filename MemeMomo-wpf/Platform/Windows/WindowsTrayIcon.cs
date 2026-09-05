using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MemeMomo.Services;
using WpfApplication = System.Windows.Application;

namespace MemeMomo.Platform.Windows;

/// <summary>NotifyIcon adapter with the source single/double-click semantics.</summary>
public sealed class WindowsTrayIcon : IDisposable, IBalloonNotificationSink
{
    // 自 Vista 起 Shell 忽略该超时，实际显示时长由系统「通知显示时长」设置决定；节拍由 ReminderToastQueue 看门狗负责。
    private const int BalloonTimeoutMilliseconds = 10000;

    private readonly NotifyIcon _notifyIcon;
    private readonly Action _showMenu;
    private readonly Action _showWindow;
    private bool _traySingleClickToShow;
    private long _lastShowTick;
    private int _disposed;

    /// <summary>气泡被点击（含通知中心历史条目，Shell 是否回传由系统决定）。</summary>
    public event Action? BalloonClicked;

    /// <summary>气泡被用户关闭或超时消失（Win10/11 下不保证到达）。</summary>
    public event Action? BalloonDismissed;

    public WindowsTrayIcon(Action showMenu, Action showWindow)
    {
        _showMenu = showMenu ?? throw new ArgumentNullException(nameof(showMenu));
        _showWindow = showWindow ?? throw new ArgumentNullException(nameof(showWindow));
        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "MemeMomo",
            Visible = true
        };
        _notifyIcon.MouseUp += OnMouseUp;
        _notifyIcon.MouseClick += OnMouseClick;
        _notifyIcon.MouseDoubleClick += OnMouseDoubleClick;
        _notifyIcon.BalloonTipClicked += OnBalloonTipClicked;
        _notifyIcon.BalloonTipClosed += OnBalloonTipClosed;
    }

    /// <summary>通过常驻托盘图标发一条 Shell 气泡（即 Windows 系统通知）。失败只记调试输出，不得打断队列节拍。</summary>
    public void ShowBalloon(string title, string body)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = body;
            _notifyIcon.BalloonTipIcon = ToolTipIcon.None;
            _notifyIcon.ShowBalloonTip(BalloonTimeoutMilliseconds);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[Tray] balloon show failed: {exception.Message}");
        }
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

    private void OnBalloonTipClicked(object? sender, EventArgs e) => BalloonClicked?.Invoke();

    private void OnBalloonTipClosed(object? sender, EventArgs e) => BalloonDismissed?.Invoke();

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
        _notifyIcon.BalloonTipClicked -= OnBalloonTipClicked;
        _notifyIcon.BalloonTipClosed -= OnBalloonTipClosed;
        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
    }

    private static Icon LoadIcon()
    {
        try
        {
            Uri uri = new("pack://application:,,,/MemeMomo;component/Assets/appicon.ico", UriKind.Absolute);
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

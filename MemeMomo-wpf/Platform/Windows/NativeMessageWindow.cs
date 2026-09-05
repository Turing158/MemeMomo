using System.Windows.Interop;

namespace MemeMomo.Platform.Windows;

/// <summary>Hidden top-level HWND used for WM_HOTKEY and the restore broadcast.</summary>
internal sealed class NativeMessageWindow : IDisposable
{
    private const int WmHotkey = 0x0312;
    private readonly HwndSource _source;
    private readonly uint _restoreMessage;
    private int _disposed;

    internal NativeMessageWindow()
    {
        HwndSourceParameters parameters = new("MemeMomo.NativeMessageWindow")
        {
            ParentWindow = nint.Zero,
            WindowStyle = 0,
            ExtendedWindowStyle = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        _restoreMessage = SingleInstance.GetRestoreMessageId();
    }

    internal nint Handle => _source.Handle;

    internal event Action<int>? HotkeyPressed;
    internal event Action? RestoreRequested;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
        HotkeyPressed = null;
        RestoreRequested = null;
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey)
        {
            HotkeyPressed?.Invoke(wParam.ToInt32());
            handled = true;
        }
        else if (_restoreMessage != 0 && message == _restoreMessage)
        {
            RestoreRequested?.Invoke();
            handled = true;
        }

        return nint.Zero;
    }
}

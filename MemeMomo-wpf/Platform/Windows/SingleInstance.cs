using System.Runtime.InteropServices;

namespace MemeMomo.Platform.Windows;

/// <summary>
/// Owns the production process mutex and the registered restore message.
/// The mutex is acquired before WPF is constructed by <see cref="Program"/>.
/// </summary>
public static class SingleInstance
{
    public const string MutexName = "MemeMomoAppSingleInstance";
    public const string RestoreMessageName = "MemeMomo.RestoreInstance.v1";

    // Win32 HWND_BROADCAST is the unsigned 16-bit value 0xFFFF.
    private static readonly nint HwndBroadcast = new(0xffff);
    private static Mutex? _mutex;
    private static uint _restoreMessageId;

    public static bool TryAcquire(out Mutex mutex)
    {
        Mutex candidate = new(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            candidate.Dispose();
            mutex = null!;
            return false;
        }

        _mutex = candidate;
        mutex = candidate;
        return true;
    }

    public static void Release()
    {
        Mutex? mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The process is already exiting or ownership was transferred.
        }
        finally
        {
            mutex.Dispose();
        }
    }

    public static uint GetRestoreMessageId()
    {
        if (_restoreMessageId != 0)
        {
            return _restoreMessageId;
        }

        _restoreMessageId = RegisterWindowMessage(RestoreMessageName);
        return _restoreMessageId;
    }

    public static bool NotifyExistingInstance()
    {
        uint message = GetRestoreMessageId();
        return message != 0 && PostMessage(HwndBroadcast, message, nint.Zero, nint.Zero);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, uint message, nint wParam, nint lParam);
}

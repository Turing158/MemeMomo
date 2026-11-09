using System.Runtime.InteropServices;
using DataObject = System.Windows.DataObject;
using FormsClipboard = System.Windows.Forms.Clipboard;
using FormsDataObject = System.Windows.Forms.DataObject;

namespace MemeMomo.Platform.Windows;

internal static class TextClipboard
{
    private static long _copyVersion;

    internal static void CancelPendingCopy() => Interlocked.Increment(ref _copyVersion);

    internal static Task<bool> CopyAsync(DataObject data, CancellationToken cancellationToken)
        => CopyAsync(data, cancellationToken, TryWrite);

    internal static Task<bool> CopyAsync(DataObject data, CancellationToken cancellationToken,
        Func<FormsDataObject, bool> write)
    {
        FormsDataObject snapshot = new();
        // Keep HTML, System.String and AvalonEdit's whole-line flag as well as text.
        foreach (string format in data.GetFormats(autoConvert: false))
            snapshot.SetData(format, autoConvert: false, data.GetData(format, autoConvert: false));
        long version = Interlocked.Increment(ref _copyVersion);
        return WriteAsync(snapshot, version, cancellationToken, write);
    }

    private static async Task<bool> WriteAsync(FormsDataObject data, long version,
        CancellationToken cancellationToken, Func<FormsDataObject, bool> write)
    {
        for (int attempt = 0; attempt < 21; attempt++)
        {
            if (cancellationToken.IsCancellationRequested || version != Volatile.Read(ref _copyVersion)) return false;
            if (write(data)) return true;
            if (attempt == 20) return false;
            try
            {
                await Task.Delay(50, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return false;
    }

    private static bool TryWrite(FormsDataObject data)
    {
        try
        {
            // WPF's Clipboard API sleeps on the UI thread when another process owns
            // the clipboard. WinForms exposes the retry count; await retries above.
            FormsClipboard.SetDataObject(data, copy: true, retryTimes: 0, retryDelay: 0);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}

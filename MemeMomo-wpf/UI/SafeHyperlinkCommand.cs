using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;

namespace MemeMomo.UI;

internal sealed class SafeHyperlinkCommand : ICommand
{
    private readonly Action<Uri> _launch;

    public SafeHyperlinkCommand(Action<Uri>? launch = null) =>
        _launch = launch ?? Launch;

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => TryGetSafeUri(parameter, out _);

    public void Execute(object? parameter)
    {
        if (!TryGetSafeUri(parameter, out Uri? uri))
        {
            return;
        }

        try
        {
            _launch(uri);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"[UI] opening hyperlink failed: {exception.Message}");
        }
    }

    private static void Launch(Uri uri)
    {
        using Process? process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true
        });
    }

    internal static bool TryGetSafeUri(object? parameter, out Uri uri)
    {
        string? value = parameter switch
        {
            Uri typed => typed.OriginalString,
            _ => parameter?.ToString()
        };
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri!))
        {
            return false;
        }

        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase);
    }
}

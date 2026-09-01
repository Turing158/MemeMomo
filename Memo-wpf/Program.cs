using System.Diagnostics;
using Memo.Platform.Windows;
using WinFormsApplication = System.Windows.Forms.Application;
using WinFormsHighDpiMode = System.Windows.Forms.HighDpiMode;

namespace Memo;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!WinFormsApplication.SetHighDpiMode(WinFormsHighDpiMode.PerMonitorV2))
        {
            Trace.TraceWarning("The process DPI awareness context was already initialized.");
        }

        bool ownsMutex = SingleInstance.TryAcquire(out _);
        if (!ownsMutex)
        {
            SingleInstance.NotifyExistingInstance();
            return 0;
        }

        try
        {
            App application = new();
            application.InitializeComponent();
            return application.Run();
        }
        finally
        {
            if (ownsMutex)
            {
                SingleInstance.Release();
            }
        }
    }
}

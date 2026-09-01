using Memo.Models;
using System.Threading.Tasks;

namespace Memo.Services;

/// <summary>
/// Host callbacks owned by the application shell. MainWindow keeps its memo
/// workflow independent from settings, tray and process-lifetime integrations.
/// </summary>
public interface IMainWindowHostActions
{
    void OpenSettings(WindowContext context);
    void MinimizeToTray(WindowContext context);
    void SetTaskbarIconVisible(WindowContext context, bool visible);
    Task<CloseButtonAction?> AskCloseButtonAction(WindowContext context);
    void ExitApplication(WindowContext context);
}

/// <summary>Small context passed to host callbacks without exposing MainWindow internals.</summary>
public sealed class WindowContext
{
    internal WindowContext(object window) => Window = window;
    internal object Window { get; }
}

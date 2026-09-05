namespace MemeMomo.Services;

/// <summary>Commands exposed by the application shell to the custom tray flyout.</summary>
public interface ITrayMenuHostActions
{
    bool IsMainWindowPinned { get; }
    void OpenMainWindow();
    void CreateNewMemo();
    void ToggleMainWindowPinned();
    void ExitApplication();
}

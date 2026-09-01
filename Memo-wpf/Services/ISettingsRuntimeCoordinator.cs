using System.Windows;
using Memo.Models;

namespace Memo.Services;

/// <summary>Runtime effects owned by App and consumed by the settings window.</summary>
public interface ISettingsRuntimeCoordinator
{
    void Preview(AppSettings settings);
    Task SaveAsync(AppSettings settings);
    void OpenTutorial(AppSettings settings);
    void ReportSaveFailure(Window owner, Exception exception);
}

internal sealed class DelegateSettingsRuntimeCoordinator(
    Action<AppSettings> preview,
    Func<AppSettings, Task> saveAsync,
    Action<AppSettings>? openTutorial = null,
    Action<Window, Exception>? reportSaveFailure = null) : ISettingsRuntimeCoordinator
{
    public void Preview(AppSettings settings) => preview(settings);
    public Task SaveAsync(AppSettings settings) => saveAsync(settings);
    public void OpenTutorial(AppSettings settings) => openTutorial?.Invoke(settings);
    public void ReportSaveFailure(Window owner, Exception exception) => reportSaveFailure?.Invoke(owner, exception);
}

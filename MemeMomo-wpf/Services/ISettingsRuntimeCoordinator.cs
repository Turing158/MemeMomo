using System.Windows;
using MemeMomo.Models;

namespace MemeMomo.Services;

/// <summary>Runtime effects owned by App and consumed by its hosted settings session.</summary>
public interface ISettingsRuntimeCoordinator
{
    void Preview(AppSettings settings);
    Task SaveAsync(AppSettings settings);
    void OpenTutorial(AppSettings settings);
    void ReportSaveFailure(Window owner, Exception exception);
    void SetHotkeyCaptureActive(bool active) { }
}

internal sealed class DelegateSettingsRuntimeCoordinator(
    Action<AppSettings> preview,
    Func<AppSettings, Task> saveAsync,
    Action<AppSettings>? openTutorial = null,
    Action<Window, Exception>? reportSaveFailure = null,
    Action<bool>? setHotkeyCaptureActive = null) : ISettingsRuntimeCoordinator
{
    public void Preview(AppSettings settings) => preview(settings);
    public Task SaveAsync(AppSettings settings) => saveAsync(settings);
    public void OpenTutorial(AppSettings settings) => openTutorial?.Invoke(settings);
    public void ReportSaveFailure(Window owner, Exception exception) => reportSaveFailure?.Invoke(owner, exception);
    public void SetHotkeyCaptureActive(bool active) => setHotkeyCaptureActive?.Invoke(active);
}

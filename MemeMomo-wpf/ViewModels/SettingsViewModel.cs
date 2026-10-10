using MemeMomo.Models;
using MemeMomo.Services;

namespace MemeMomo.ViewModels;

public enum SettingsCategory
{
    General,
    Appearance,
    Windows,
    Tasks,
    Hotkeys,
    About
}

internal readonly record struct SettingsScrollPosition(string? Anchor, double Inset, double Offset);

/// <summary>A single, in-memory settings session, including failed writes and browsing state.</summary>
public sealed class SettingsViewModel
{
    private readonly ISettingsRuntimeCoordinator _coordinator;
    private readonly Action<Exception> _reportFailure;
    private Task _saveChain = Task.CompletedTask;
    private long _revision;
    private long _savedRevision;

    internal SettingsViewModel(AppSettings settings, ISettingsRuntimeCoordinator coordinator, Action<Exception> reportFailure)
    {
        Settings = settings.Clone();
        _coordinator = coordinator;
        _reportFailure = reportFailure;
    }

    internal AppSettings Settings { get; }
    internal Dictionary<SettingsCategory, SettingsScrollPosition> Positions { get; } = [];
    public SettingsCategory Category { get; internal set; }
    public AppSettings SettingsSnapshot => Settings.Clone();
    public string? LastSaveError { get; private set; }
    public bool HasUnsavedChanges => _savedRevision != _revision;

    internal void SynchronizeRuntime(AppSettings runtime)
    {
        // Runtime geometry is always current. A failed or queued preference snapshot
        // remains authoritative until that same serial channel has saved it.
        runtime.CopyMainWindowRuntimeStateTo(Settings);
        if (!HasUnsavedChanges) runtime.CopyUserPreferencesTo(Settings);
    }

    internal void CommitChange()
    {
        AppSettings snapshot = Settings.Clone();
        long revision = ++_revision;
        try { _coordinator.Preview(snapshot.Clone()); }
        catch (Exception error) { ReportFailure(error); }
        _saveChain = SaveAfterAsync(_saveChain, snapshot, revision);
    }

    internal void RetryLatestSnapshot()
    {
        if (HasUnsavedChanges && _saveChain.IsCompleted)
            _saveChain = SaveAfterAsync(_saveChain, Settings.Clone(), _revision);
    }

    public async Task WaitForPendingSaveAsync()
    {
        Task pending;
        do
        {
            pending = _saveChain;
            await pending;
        } while (!ReferenceEquals(pending, _saveChain));
    }

    private async Task SaveAfterAsync(Task previous, AppSettings snapshot, long revision)
    {
        await previous;
        try
        {
            await _coordinator.SaveAsync(snapshot);
            _savedRevision = revision;
            if (revision == _revision) LastSaveError = null;
        }
        catch (Exception error) { ReportFailure(error); }
    }

    private void ReportFailure(Exception error)
    {
        LastSaveError = error.Message;
        _reportFailure(error);
    }
}

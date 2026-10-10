using MemeMomo.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MemeMomo.Services;

public class JsonSettingsStorage
{
    private readonly AtomicJsonFile _file;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public JsonSettingsStorage() : this(null) { }

    internal JsonSettingsStorage(string? filePath) => _file = new(filePath ?? Path.Combine(MemoDataPaths.RootDirectory, "settings.json"));
    public string? LoadDiagnostic => _file.ReadError;

    public async Task<AppSettings> LoadAsync()
    {
        try
        {
            var settings = await _file.ReadAsync(json => JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? throw new JsonException("Expected settings object.")) ?? AppSettings.CreateDefault();
            if (string.IsNullOrWhiteSpace(settings.DefaultTaskStatusId)) settings.DefaultTaskStatusId = TaskStatusDefinition.DefaultId;
            if (!Enum.IsDefined(settings.Language)) settings.Language = AppLanguage.ChineseSimplified;
            if (!Enum.IsDefined(settings.ThemeMode)) settings.ThemeMode = ThemeMode.FollowSystem;
            if (!Enum.IsDefined(settings.MemoBackgroundColor)) settings.MemoBackgroundColor = MemoColorPreset.FollowTheme;
            if (!Enum.IsDefined(settings.MotionMode)) settings.MotionMode = MotionMode.AlwaysOn;
            if (!Enum.IsDefined(settings.ReminderNotification)) settings.ReminderNotification = ReminderNotificationMode.SystemToast;
            settings.MainWindowDockSize = Math.Clamp(
                settings.MainWindowDockSize,
                AppSettings.MinimumMainWindowDockSize,
                AppSettings.MaximumMainWindowDockSize);
            if (!settings.MainWindowDockEnabled) settings.MainWindowDocked = false;
            SanitizePopoutDock(settings);
            return settings;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsStorage] Load failed: {ex.Message}");
            return AppSettings.CreateDefault();
        }
    }

    public virtual Task<StorageResult> TrySaveAsync(AppSettings settings) =>
        _file.WriteAsync(() => JsonSerializer.Serialize(settings, JsonOptions));

    public async Task SaveAsync(AppSettings settings) => (await TrySaveAsync(settings)).ThrowIfFailed();

    private static void SanitizePopoutDock(AppSettings settings)
    {
        if (settings.PopoutDock is null)
        {
            settings.PopoutDock = new PopoutDockSettings();
            return;
        }

        if (settings.PopoutDock.PopLengths is null)
        {
            settings.PopoutDock.PopLengths = new Dictionary<string, double>(StringComparer.Ordinal);
            return;
        }

        string[] keys = settings.PopoutDock.PopLengths.Keys.ToArray();
        foreach (string key in keys)
        {
            settings.PopoutDock.PopLengths[key] = PopoutDockSettings.ClampPopLength(
                settings.PopoutDock.PopLengths[key]);
        }
    }
}

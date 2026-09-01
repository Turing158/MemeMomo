using Memo.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Memo.Services;

public class JsonSettingsStorage
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public JsonSettingsStorage() : this(null) { }

    internal JsonSettingsStorage(string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (directory != null) Directory.CreateDirectory(directory);
            _filePath = Path.GetFullPath(filePath);
            return;
        }
        var dir = MemoDataPaths.RootDirectory;
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "settings.json");
    }

    public async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(_filePath)) return AppSettings.CreateDefault();
            var json = await File.ReadAllTextAsync(_filePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? AppSettings.CreateDefault();
            if (!Enum.IsDefined(settings.ThemeMode)) settings.ThemeMode = ThemeMode.FollowSystem;
            if (!Enum.IsDefined(settings.MotionMode)) settings.MotionMode = MotionMode.AlwaysOn;
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

    public async Task SaveAsync(AppSettings settings)
    {
        await _semaphore.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsStorage] Save failed: {ex.Message}");
        }
        finally
        {
            _semaphore.Release();
        }
    }

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

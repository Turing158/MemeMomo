using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MemeMomo.Services;

public sealed record StorageResult(bool Success, string? Error = null)
{
    public static StorageResult Ok { get; } = new(true);
    public void ThrowIfFailed() { if (!Success) throw new IOException(Error); }
}

internal sealed class AtomicJsonFile(string path)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    internal static JsonSerializerOptions Options { get; } = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal string FilePath { get; } = Path.GetFullPath(path);
    internal string? ReadError { get; private set; }
    internal bool WriteBlocked { get; set; }

    internal async Task<T?> ReadAsync<T>(Func<string, T> parse)
    {
        if (!File.Exists(FilePath)) return default;
        try { return parse(await File.ReadAllTextAsync(FilePath)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            ReadError = $"{FilePath}: {error.Message}";
            try
            {
                File.Copy(FilePath, FilePath + ".damaged-" + Guid.NewGuid().ToString("N"));
                if (File.Exists(FilePath + ".bak"))
                {
                    T recovered = parse(await File.ReadAllTextAsync(FilePath + ".bak"));
                    File.Copy(FilePath + ".bak", FilePath + ".recovered-" + Guid.NewGuid().ToString("N"));
                    return recovered;
                }
            }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { ReadError += $"; {backupError.Message}"; }
            WriteBlocked = true;
            return default;
        }
    }

    internal async Task<StorageResult> WriteAsync(Func<string> serialize, string? upgradeBackup = null)
    {
        var gate = Gates.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        string temporary = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            if (WriteBlocked) return new(false, LocalizationService.Format("数据文件需要恢复后才能保存：{0}", FilePath));
            string json = serialize();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (upgradeBackup is not null && File.Exists(FilePath) && !File.Exists(FilePath + upgradeBackup)) File.Copy(FilePath, FilePath + upgradeBackup);
            if (File.Exists(FilePath) && !File.Exists(FilePath + ".pre-tasks.bak")) File.Copy(FilePath, FilePath + ".pre-tasks.bak");
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
            return StorageResult.Ok;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(false, error.Message); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            gate.Release();
        }
    }
}

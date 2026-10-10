using System.IO;
using System.Text.Json;
using MemeMomo.Models;

namespace MemeMomo.Services;

public class JsonTaskStatusStorage
{
    private readonly AtomicJsonFile _file;
    public List<string> Diagnostics { get; } = [];
    public JsonTaskStatusStorage(string? filePath = null) => _file = new(filePath ?? Path.Combine(MemoDataPaths.RootDirectory, "task-statuses.json"));

    public async Task<IReadOnlyList<TaskStatusDefinition>> LoadAsync()
    {
        var result = await _file.ReadAsync(Parse) ?? [];
        if (_file.ReadError is { } error) Diagnostics.Add(error);
        return result;
    }

    private List<TaskStatusDefinition> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1
            || !root.TryGetProperty("statuses", out var statuses) || statuses.ValueKind != JsonValueKind.Array)
            throw new JsonException("Unsupported task status catalog version or shape.");
        List<TaskStatusDefinition> result = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var entry in statuses.EnumerateArray())
        {
            try
            {
                var status = entry.Deserialize<TaskStatusDefinition>(AtomicJsonFile.Options);
                if (status is null || string.IsNullOrWhiteSpace(status.Id) || !Enum.IsDefined(status.Category)
                    || (status.IsBuiltIn && status.Id != TaskStatusDefinition.BuiltInId(status.Category)) || !ids.Add(status.Id))
                    throw new JsonException("Invalid or duplicate status identity/category.");
                result.Add(status);
            }
            catch (JsonException error)
            {
                Diagnostics.Add(error.Message + " " + entry.GetRawText());
                // Do not destroy rejected entries on the next ordinary save.
                _file.WriteBlocked = true;
            }
        }
        return result;
    }

    public virtual Task<StorageResult> TrySaveAsync(IEnumerable<TaskStatusDefinition> statuses) =>
        _file.WriteAsync(() => JsonSerializer.Serialize(new { schemaVersion = 1, statuses = statuses.ToArray() }, AtomicJsonFile.Options));
}

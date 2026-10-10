using MemeMomo.Models;
using System.IO;
using System.Text.Json;

namespace MemeMomo.Services;

public class JsonMemoStorage
{
    private readonly AtomicJsonFile _file;
    public JsonMemoStorage() : this(null) { }
    internal JsonMemoStorage(string? filePath) => _file = new(filePath ?? Path.Combine(MemoDataPaths.RootDirectory, "memos.json"));
    public string? LoadDiagnostic => _file.ReadError;
    public async Task<List<MemoItem>> LoadAsync() => await _file.ReadAsync(Parse) ?? [];
    private static List<MemoItem> Parse(string json)
    {
        var items = JsonSerializer.Deserialize<List<MemoItem>>(json, AtomicJsonFile.Options)
            ?? throw new JsonException("Expected a memo array.");
        if (items.Any(item => item is null) || items.Select(item => item.Id).Distinct().Count() != items.Count)
            throw new JsonException("Duplicate memo IDs or null records require data recovery.");
        return items;
    }
    public virtual Task<StorageResult> TrySaveAsync(IEnumerable<MemoItem> items) =>
        _file.WriteAsync(() =>
        {
            var snapshot = items.ToList();
            if (snapshot.Select(item => item.Id).Distinct().Count() != snapshot.Count)
                throw new JsonException("Duplicate memo IDs require data recovery.");
            return JsonSerializer.Serialize(snapshot, AtomicJsonFile.Options);
        }, ".pre-recycle-bin.bak");
    public async Task SaveAsync(IEnumerable<MemoItem> items) => (await TrySaveAsync(items)).ThrowIfFailed();
}

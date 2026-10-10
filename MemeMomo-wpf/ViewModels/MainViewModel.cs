using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.Utils;
using System.Collections.ObjectModel;

namespace MemeMomo.ViewModels;

public partial class MainViewModel
{
    private readonly JsonMemoStorage _storage;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Task? _loadTask;
    public ObservableCollection<MemoItem> Memos { get; } = new();
    public MemoTaskListView TaskView { get; }
    public MemoTaskService Tasks { get; }
    public MemoRecycleBinService RecycleBin { get; }
    public RecycleBinViewModel RecycleView { get; }
    public event Action<Guid>? MemoDeleted;
    public event Action? MemosLoaded;
    public event Action<string>? SaveFailed;
    public bool IsLoaded { get; private set; }
    public string? LoadDiagnostic => _storage.LoadDiagnostic;
    public Guid? EditingId { get; private set; }
    public MainViewModel() : this(new JsonMemoStorage()) { }
    internal MainViewModel(JsonMemoStorage storage, TaskStatusCatalog? catalog = null, AppSettings? settings = null,
        Func<Task<StorageResult>>? saveSettings = null)
    {
        _storage = storage;
        Tasks = new MemoTaskService(this, catalog ?? new(), settings ?? new(), saveSettings);
        TaskView = new MemoTaskListView(Memos, Tasks.Catalog);
        DeletedMemos = new(_deletedMemos);
        RecycleBin = new(this);
        RecycleView = new(DeletedMemos);
        Memos.CollectionChanged += (_, _) => Tasks.NotifyMemosChanged();
    }
    public Task LoadAsync() => _loadTask ??= LoadCoreAsync();
    private async Task LoadCoreAsync()
    {
        var items = await _storage.LoadAsync();
        foreach (var item in items)
        {
            // Storage validates duplicate IDs within the file. An already populated
            // view model may have saved before its window's first Loaded event.
            if (Memos.Concat(_deletedMemos).Any(existing => existing.Id == item.Id)) continue;
            if (item.IsDeleted) _deletedMemos.Add(item);
            else { Tasks.Catalog.Normalize(item); Memos.Add(item); }
        }
        IsLoaded = true;
        MemosLoaded?.Invoke();
    }
    public MemoItem? FindByContent(string content) => Memos.FirstOrDefault(m => m.Content == content);
    public MemoItem? AddOrPromoteItem(string content)
    {
        var existing = FindByContent(content);
        if (existing != null) { MoveToFront(existing.Id); return existing; }
        return AddItem(content);
    }
    private MemoItem CreateItem(string content, MemoKind? kind = null, string? statusId = null)
    {
        var now = DateTimeUtils.Now;
        var item = new MemoItem { Content = content, CreatedAt = now, UpdatedAt = now };
        Tasks.InitializeNew(item, kind, statusId);
        Memos.Insert(0, item);
        return item;
    }
    public MemoItem? AddItem(string content)
    {
        var item = CreateItem(content);
        SaveInBackground();
        return item;
    }
    public async Task<MemoItem?> AddItemAndSaveAsync(string content, MemoKind? kind = null, string? statusId = null)
    {
        var item = CreateItem(content, kind, statusId);
        try { await SaveAsync(); return item; }
        catch { Memos.Remove(item); throw; }
    }
    public void UpdateItem(Guid id, string content)
    {
        var item = Memos.FirstOrDefault(m => m.Id == id);
        if (item == null || item.IsLifecyclePending) return;
        if (item.Content == content) return;
        item.Content = content;
        item.UpdatedAt = DateTimeUtils.Now;
    }
    public void UpdateItemAndSave(Guid id, string content) { UpdateItem(id, content); SaveInBackground(); }
    public async Task UpdateItemAndSaveAsync(Guid id, string content) { UpdateItem(id, content); await SaveAsync(); }
    public async Task CompleteItemEditAndSaveAsync(Guid id, string content)
    {
        var item = Memos.FirstOrDefault(m => m.Id == id);
        if (item == null) return;
        UpdateItem(id, content);
        var index = Memos.IndexOf(item);
        if (index > 0) Memos.Move(index, 0);
        if (EditingId == id) EditingId = null;
        await SaveAsync();
    }
    public async Task<StorageResult> TrySaveAsync()
    {
        await _saveGate.WaitAsync();
        try { return await _storage.TrySaveAsync(FullSnapshot()); }
        finally { _saveGate.Release(); }
    }
    public async Task SaveAsync() => (await TrySaveAsync()).ThrowIfFailed();
    private async void SaveInBackground()
    {
        var result = await TrySaveAsync();
        if (!result.Success) SaveFailed?.Invoke(result.Error ?? string.Empty);
    }
    // All body, reminder and task writes share this gate. Candidate metadata never
    // enters the live objects before durability, so failed writes need no rollback.
    internal async Task<StorageResult> CommitMetadataAsync(Func<MemoItem, MemoItem?> createCandidate)
    {
        await _saveGate.WaitAsync();
        Dictionary<Guid, (MemoItem Live, MemoItem Candidate, DateTime OriginalUpdatedAt)> changes = [];
        try
        {
            foreach (var item in Memos)
            {
                if (createCandidate(item) is not { } candidate) continue;
                if (item.IsLifecyclePending) return new(false, LocalizationService.Get("正在保存，请稍后重试。"));
                changes[item.Id] = (item, candidate, item.UpdatedAt);
            }
            if (changes.Count == 0) return StorageResult.Ok;
            foreach (var change in changes.Values) change.Live.IsTaskCommitPending = true;
            var snapshot = Memos.Select(item => changes.TryGetValue(item.Id, out var change) ? change.Candidate : item.PersistenceCopy())
                .Concat(_deletedMemos.Select(item => item.PersistenceCopy())).ToArray();
            var result = await _storage.TrySaveAsync(snapshot);
            if (!result.Success) return result;
            foreach (var (live, candidate, originalUpdatedAt) in changes.Values)
            {
                if (!Memos.Contains(live)) continue;
                live.Kind = candidate.Kind;
                live.TaskStatusId = candidate.TaskStatusId;
                live.TaskStatusCategorySnapshot = candidate.TaskStatusCategorySnapshot;
                live.TaskClosedAt = candidate.TaskClosedAt;
                live.LastActiveTaskStatusId = candidate.LastActiveTaskStatusId;
                live.ReminderAt = candidate.ReminderAt;
                // Preserve an update timestamp from a body edit made during I/O.
                if (live.UpdatedAt == originalUpdatedAt && live.Content == candidate.Content) live.UpdatedAt = candidate.UpdatedAt;
            }
            return result;
        }
        finally
        {
            foreach (var change in changes.Values) change.Live.IsTaskCommitPending = false;
            _saveGate.Release();
        }
    }
    public void MoveToFront(Guid id)
    {
        var item = Memos.FirstOrDefault(m => m.Id == id);
        if (item == null) return;
        var index = Memos.IndexOf(item);
        if (index > 0) { Memos.Move(index, 0); SaveInBackground(); }
    }
    public void MoveItem(Guid id, int newIndex)
    {
        if (!TaskView.CanReorder || newIndex < 0 || newIndex >= Memos.Count) return;
        var item = Memos.FirstOrDefault(m => m.Id == id);
        if (item == null) return;
        var oldIndex = Memos.IndexOf(item);
        if (oldIndex < 0 || oldIndex == newIndex) return;
        Memos.Move(oldIndex, newIndex);
        SaveInBackground();
    }
    public Task<StorageResult> DeleteItem(Guid id) => RecycleBin.DeleteAsync(id);
    public void BeginEdit(Guid id) => EditingId = id;
    public void EndEdit() => EditingId = null;
}

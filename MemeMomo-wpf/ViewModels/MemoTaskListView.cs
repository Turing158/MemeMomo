using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using MemeMomo.Models;
using MemeMomo.Services;

namespace MemeMomo.ViewModels;

public sealed class MemoTaskListView : INotifyPropertyChanged
{
    private readonly ObservableCollection<MemoItem> _source;
    private readonly TaskStatusCatalog _catalog;
    private readonly HashSet<MemoItem> _observed = [];
    private MemoKind? _kind;
    private string? _status;
    public ObservableCollection<MemoItem> Items { get; } = [];
    public MemoKind? Kind { get => _kind; set { _kind = value; if (value == MemoKind.Note) _status = null; Refresh(); } }
    // null = all; active = the three active categories; category:N or status ID.
    public string? Status { get => _status; set { _status = value; if (value is not null) _kind = MemoKind.Task; Refresh(); } }
    public bool CanReorder => Kind is null && Status is null;
    public int VisibleCount => Items.Count;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;
    public MemoTaskListView(ObservableCollection<MemoItem> source, TaskStatusCatalog catalog)
    {
        _source = source; _catalog = catalog;
        source.CollectionChanged += OnSourceChanged;
        catalog.Changed += OnCatalogChanged;
        Refresh();
    }
    public void Clear() { _kind = null; _status = null; Refresh(); }
    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();
    private void OnCatalogChanged()
    {
        if (_status is not null && _status != "active" && !_status.StartsWith("category:", StringComparison.Ordinal) && _catalog.Find(_status) is null)
            _status = null;
        Refresh();
    }
    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MemoItem.Kind) or nameof(MemoItem.TaskStatusId) or nameof(MemoItem.TaskStatusCategorySnapshot)) Refresh();
    }
    public void Refresh()
    {
        foreach (var item in _observed.Where(item => !_source.Contains(item)).ToArray()) { item.PropertyChanged -= OnItemChanged; _observed.Remove(item); }
        foreach (var item in _source.Where(item => !_observed.Contains(item))) { item.PropertyChanged += OnItemChanged; _observed.Add(item); }
        var next = _source.Where(Matches).ToArray();
        // Preserve existing containers when no visible membership/order changed.
        if (!Items.SequenceEqual(next))
        {
            for (int index = Items.Count - 1; index >= 0; index--) if (!next.Contains(Items[index])) Items.RemoveAt(index);
            for (int index = 0; index < next.Length; index++)
            {
                int old = Items.IndexOf(next[index]);
                if (old < 0) Items.Insert(index, next[index]);
                else if (old != index) Items.Move(old, index);
            }
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        Changed?.Invoke();
    }
    private bool Matches(MemoItem item)
    {
        if (Kind is { } kind && item.Kind != kind) return false;
        if (Status is null) return true;
        if (!item.IsTask) return false;
        if (Status == "active") return !item.IsTaskClosed;
        if (Status.StartsWith("category:", StringComparison.Ordinal)) return Status == $"category:{(int)(item.TaskStatusCategorySnapshot ?? TaskStatusCategory.Todo)}";
        return item.TaskStatusId == Status;
    }
}

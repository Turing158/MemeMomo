using System.Collections.ObjectModel;
using System.Collections.Specialized;
using MemeMomo.Models;

namespace MemeMomo.ViewModels;

public sealed class RecycleBinViewModel
{
    private readonly ReadOnlyObservableCollection<MemoItem> _source;
    public ObservableCollection<MemoItem> Items { get; } = [];
    public MemoItem? SelectedItem { get; private set; }
    public double ScrollOffset { get; set; }
    public event Action? Changed;
    internal RecycleBinViewModel(ReadOnlyObservableCollection<MemoItem> source)
    {
        _source = source;
        ((INotifyCollectionChanged)source).CollectionChanged += (_, _) => Refresh();
        Refresh();
    }
    public void Select(MemoItem? item)
    {
        if (item is not null && !Items.Contains(item)) return;
        SelectedItem = item;
        Changed?.Invoke();
    }
    public void EnsureSelection() { if (SelectedItem is null) Select(Items.FirstOrDefault()); }
    private void Refresh()
    {
        int index = SelectedItem is null ? 0 : Math.Max(0, Items.IndexOf(SelectedItem));
        var sorted = _source.OrderByDescending(item => item.DeletedAt).ThenBy(item => item.Id).ToArray();
        foreach (var removed in Items.Except(sorted).ToArray()) Items.Remove(removed);
        for (int i = 0; i < sorted.Length; i++)
        {
            int existing = Items.IndexOf(sorted[i]);
            if (existing < 0) Items.Insert(i, sorted[i]); else if (existing != i) Items.Move(existing, i);
        }
        if (SelectedItem is not null && !Items.Contains(SelectedItem))
            SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        Changed?.Invoke();
    }
}

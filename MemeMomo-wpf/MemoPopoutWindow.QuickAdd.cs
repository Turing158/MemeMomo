using System.ComponentModel;
using System.Windows;
using MemeMomo.Components;
using MemeMomo.Components.Dialogs;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.UI.Notifications;

namespace MemeMomo;

public partial class MemoPopoutWindow
{
    private Func<string, MemoKind, string?, Task<MemoItem?>>? _createQuickMemo;
    private bool _quickAddSaving;
    private bool _quickClosePending;
    private bool _quickCloseApproved;
    private bool _quickCloseScheduled;

    internal bool IsQuickAddDraft => _createQuickMemo is not null;

    internal MemoPopoutWindow(
        System.Windows.Point position,
        Func<string, MemoKind, string?, Task<MemoItem?>> createMemo,
        Func<MemoItem, string, Task> saveMemo) : this(new MemoItem(), position, saveMemo)
    {
        _createQuickMemo = createMemo;
        UpdateMemoVisuals();
    }

    private async Task<bool> SaveQuickAddDraftAsync(MarkdownSaveRequest request)
    {
        if (_createQuickMemo is not { } create || _quickAddSaving || _cleanupDone) return false;
        _quickAddSaving = true;
        MarkdownEditor.IsEnabled = false;
        try
        {
            MemoItem? saved = await create(request.Markdown, TaskStatusSelector.NewKind, TaskStatusSelector.NewStatusId);
            if (saved is null) return false;
            MemoItem draft = Memo;
            draft.PropertyChanged -= OnMemoPropertyChanged;
            if (draft.PopoutRefCount > 0)
            {
                draft.PopoutRefCount--;
                saved.PopoutRefCount++;
            }
            _memo = saved;
            _createQuickMemo = null;
            DataContext = saved;
            saved.PropertyChanged += OnMemoPropertyChanged;
            TaskStatusSelector.Memo = saved;
            InitializeDockController();
            await BeginEditAsync();
            UpdateMemoVisuals();
            return true;
        }
        finally
        {
            _quickAddSaving = false;
            MarkdownEditor.IsEnabled = true;
        }
    }

    internal async Task<bool> RequestQuickAddCloseAsync()
    {
        if (_quickClosePending || _quickAddSaving) return false;
        if (_quickCloseApproved || !IsQuickAddDraft || _cleanupDone) return true;
        _quickClosePending = true;
        try
        {
            int answer = await WindowNotifications.ChooseAsync(this,
                LocalizationService.Get("保存便签？") + "\n" + LocalizationService.Get("此便签尚未添加，是否保存后关闭？"),
                LocalizationService.Get("取消"), LocalizationService.Get("不保存"), LocalizationService.Get("保存"));
            SaveDraftResult choice = answer == 2 ? SaveDraftResult.Save : answer == 1 ? SaveDraftResult.Discard : SaveDraftResult.Cancel;
            if (choice == SaveDraftResult.Cancel) return false;
            if (choice == SaveDraftResult.Save && !await MarkdownEditor.CompleteEditingAsync()) return false;
            _quickCloseApproved = true;
            CloseWithTransition();
            return true;
        }
        finally { _quickClosePending = false; }
    }

}

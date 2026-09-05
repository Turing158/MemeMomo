using System.Collections.Specialized;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Rendering;
using MemeMomo.Components.Dialogs;
using MemeMomo.Editor;
using MemeMomo.Infrastructure;
using MemeMomo.Markdown;
using MemeMomo.Services;
using MemeMomo.UI;
using MemeMomo.UI.Popup;
using Microsoft.Win32;
using WinFormsClipboard = System.Windows.Forms.Clipboard;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using DragEventArgs = System.Windows.DragEventArgs;
using IDataObject = System.Windows.IDataObject;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using UserControl = System.Windows.Controls.UserControl;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Point = System.Windows.Point;

namespace MemeMomo.Components;

public sealed class MarkdownToolbarCommandEventArgs(string command) : EventArgs
{
    public string Command { get; } = command;
}

public partial class MarkdownEditor : UserControl, IDisposable
{
    public static readonly DependencyProperty UseBorderlessChromeProperty = DependencyProperty.Register(
        nameof(UseBorderlessChrome), typeof(bool), typeof(MarkdownEditor),
        new FrameworkPropertyMetadata(false, OnChromeChanged));

    public static readonly DependencyProperty HideToolbarInPreviewProperty = DependencyProperty.Register(
        nameof(HideToolbarInPreview), typeof(bool), typeof(MarkdownEditor),
        new FrameworkPropertyMetadata(false, OnToolbarVisibilityChanged));

    public static readonly DependencyProperty ShowNewActionProperty = DependencyProperty.Register(
        nameof(ShowNewAction), typeof(bool), typeof(MarkdownEditor),
        new FrameworkPropertyMetadata(false, OnShowNewActionChanged));

    public static readonly DependencyProperty BlendBordersInPreviewProperty = DependencyProperty.Register(
        nameof(BlendBordersInPreview), typeof(bool), typeof(MarkdownEditor),
        new FrameworkPropertyMetadata(false, OnBlendBordersChanged));

    public static readonly DependencyProperty WatermarkProperty = DependencyProperty.Register(
        nameof(Watermark), typeof(string), typeof(MarkdownEditor),
        new FrameworkPropertyMetadata("写点什么…", OnWatermarkChanged));

    public static readonly DependencyProperty EditorContentProperty = DependencyProperty.Register(
        nameof(EditorContent), typeof(object), typeof(MarkdownEditor));

    private readonly MarkdownImageStore _imageStore;
    private readonly AvalonEditMarkdownAdapter _adapter;
    private readonly MarkdownEditSession _editSession = new();
    private readonly DebouncedAction _autoSave;
    private readonly DispatcherTimer _statusTimer;
    private readonly MarkdownToolbarController _toolbarController;
    private Window? _ownerWindow;
    private MarkdownVisualSpan? _hoveredCodeBlock;
    private bool _saveInProgress;
    private bool _dropInProgress;
    private int _disposed;

    public MarkdownEditor() : this(null, null) { }

    internal MarkdownEditor(string? imageRoot, HttpMessageHandler? imageHttpHandler)
    {
        _imageStore = new MarkdownImageStore(imageRoot);
        InitializeComponent();
        _adapter = new AvalonEditMarkdownAdapter(_imageStore.RootDirectory, imageHttpHandler);
        _adapter.MarkdownChanged += OnAdapterMarkdownChanged;
        _adapter.SaveRequested += OnAdapterSaveRequested;
        _adapter.CancelRequested += OnAdapterCancelRequested;
        _adapter.LinkEditRequested += OnAdapterLinkEditRequested;
        _adapter.PasteImagesRequested += OnAdapterPasteImagesRequested;
        _adapter.Editor.PreviewMouseLeftButtonDown += OnEditorPreviewMouseLeftButtonDown;
        EditorHost.MouseMove += OnEditorHostMouseMove;
        EditorHost.MouseLeave += OnEditorHostMouseLeave;
        TextView textView = _adapter.Editor.TextArea.TextView;
        textView.QueryCursor += OnTextViewQueryCursor;
        textView.ScrollOffsetChanged += OnTextViewScrollOffsetChanged;
        textView.VisualLinesChanged += OnTextViewVisualLinesChanged;
        SetCurrentValue(EditorContentProperty, _adapter.View);
        _autoSave = new DebouncedAction(TimeSpan.FromMilliseconds(500));
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _statusTimer.Tick += OnStatusTimerTick;
        _toolbarController = new MarkdownToolbarController(
            Toolbar,
            FormatToolbar,
            ToolbarActions,
            EditSourceButton,
            MoreButton,
            EditSourceMenuItem,
            ResponsiveMenuSeparator,
            [
                (TableButton, TableMenuItem),
                (BulletListButton, BulletListMenuItem),
                (OrderedListButton, OrderedListMenuItem),
                (TaskListButton, TaskListMenuItem),
                (QuoteButton, QuoteMenuItem),
                (InlineCodeButton, InlineCodeMenuItem),
                (CodeBlockButton, CodeBlockMenuItem),
                (HorizontalRuleButton, HorizontalRuleMenuItem),
                (LinkButton, LinkMenuItem),
                (LocalImageButton, LocalImageMenuItem),
                (RemoteImageButton, RemoteImageMenuItem)
            ]);
        BuildTableSizePicker();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        BeginNew();
    }

    public bool UseBorderlessChrome
    {
        get => (bool)GetValue(UseBorderlessChromeProperty);
        set => SetValue(UseBorderlessChromeProperty, value);
    }

    public bool HideToolbarInPreview
    {
        get => (bool)GetValue(HideToolbarInPreviewProperty);
        set => SetValue(HideToolbarInPreviewProperty, value);
    }

    public bool ShowNewAction
    {
        get => (bool)GetValue(ShowNewActionProperty);
        set => SetValue(ShowNewActionProperty, value);
    }

    public bool BlendBordersInPreview
    {
        get => (bool)GetValue(BlendBordersInPreviewProperty);
        set => SetValue(BlendBordersInPreviewProperty, value);
    }

    public string Watermark
    {
        get => (string)GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public object? EditorContent
    {
        get => GetValue(EditorContentProperty);
        set => SetValue(EditorContentProperty, value);
    }

    public Func<MarkdownSaveRequest, Task<bool>>? SaveRequestedAsync { get; set; }
    public Func<string, Task>? CancelRequestedAsync { get; set; }
    public event EventHandler? EditRequested { add { } remove { } }
    public event EventHandler? NewRequested;
    public event Action<string>? DraftChanged;
    public event EventHandler? EditingCompleted;
    public event EventHandler<MarkdownToolbarCommandEventArgs>? ToolbarCommandInvoked;

    public bool IsEditing => true;
    public bool IsNewMemo { get; private set; }
    public string Markdown => _adapter.Markdown;
    public string AssetPathRoot => _imageStore.RootDirectory;
    internal int ImageLoadRequestCount => _adapter.ImageLoadRequestCount;
    internal int SuccessfulImageLoadCount => _adapter.SuccessfulImageLoadCount;
    internal int FailedImageLoadCount => _adapter.FailedImageLoadCount;
    internal Action<Uri>? LinkLauncher { get; set; }
    internal Func<string, Task>? CodeClipboardWriter { get; set; }

    private Func<Task<bool>>? _tableDeleteConfirmationAsync;

    /// <summary>表格删除确认钩子，转发给适配器的输入层；未设置时由输入层弹默认确认框。</summary>
    internal Func<Task<bool>>? TableDeleteConfirmationAsync
    {
        get => _tableDeleteConfirmationAsync;
        set
        {
            _tableDeleteConfirmationAsync = value;
            _adapter.TableDeleteConfirmationAsync = value;
        }
    }

    internal AvalonEditMarkdownAdapter Adapter => _adapter;
    internal Button BoldButtonPart => BoldButton;
    internal Button TableButtonPart => TableButton;
    internal FrameworkElement ToolbarPart => Toolbar;
    internal FrameworkElement ToolbarActionsPart => ToolbarActions;
    internal IReadOnlyList<Button> FormatToolbarButtons => FormatToolbar.Children
        .OfType<Button>()
        .ToArray();
    internal Popup TablePickerPopupPart => TablePickerPopup;
    internal UniformGrid TableSizeGridPart => TableSizeGrid;
    internal TextBlock TableSizeLabelPart => TableSizeLabel;

    internal static bool CanAcceptImageDrop(IEnumerable<string> files) =>
        ValidateImageFiles(files.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(), out _);

    public void BeginNew(string? draft = null)
    {
        ThrowIfDisposed();
        _autoSave.Cancel();
        IsNewMemo = true;
        _editSession.Begin(draft);
        _adapter.SetMarkdown(_editSession.Snapshot, clearUndo: true, preserveViewState: false);
        UpdateActions();
        UpdateWatermark();
        HideStatus();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (Volatile.Read(ref _disposed) == 0 && IsLoaded)
            {
                FocusEditor();
            }
        }));
    }

    public void ShowExistingPreview(string? markdown) => LoadExisting(markdown, focus: false);

    public void BeginExistingEdit(string? markdown) => LoadExisting(markdown, focus: true);

    public void SetExternalMarkdown(string? markdown)
    {
        ThrowIfDisposed();
        string next = markdown ?? string.Empty;
        if (next == Markdown)
        {
            return;
        }
        _autoSave.Cancel();
        _editSession.Begin(next);
        _adapter.SetMarkdown(next, clearUndo: true, preserveViewState: true);
        UpdateWatermark();
    }

    public void FocusEditor()
    {
        ThrowIfDisposed();
        _adapter.FocusEditor();
        _adapter.SelectSourceRange(Markdown.Length, Markdown.Length);
    }

    public Task<bool> CompleteEditingAsync() => RequestSaveAsync(completeEditing: true);

    public async Task CancelEditingAsync()
    {
        ThrowIfDisposed();
        _autoSave.Cancel();
        string restore = _editSession.Restore();
        _adapter.SetMarkdown(restore, clearUndo: true, preserveViewState: false);
        if (CancelRequestedAsync is { } callback)
        {
            await callback(restore);
        }
        if (IsNewMemo)
        {
            BeginNew();
        }
        EditingCompleted?.Invoke(this, EventArgs.Empty);
    }

    internal void AbortForSourceDeletion()
    {
        _autoSave.Cancel();
        _adapter.SetMarkdown(string.Empty, clearUndo: true, preserveViewState: false);
        UpdateWatermark();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        DetachOwnerWindow();
        _autoSave.Dispose();
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTimerTick;
        _toolbarController.Dispose();
        if (ReferenceEquals(EditorContent, _adapter.View))
        {
            SetCurrentValue(EditorContentProperty, null);
        }
        _adapter.MarkdownChanged -= OnAdapterMarkdownChanged;
        _adapter.SaveRequested -= OnAdapterSaveRequested;
        _adapter.CancelRequested -= OnAdapterCancelRequested;
        _adapter.LinkEditRequested -= OnAdapterLinkEditRequested;
        _adapter.PasteImagesRequested -= OnAdapterPasteImagesRequested;
        _adapter.Editor.PreviewMouseLeftButtonDown -= OnEditorPreviewMouseLeftButtonDown;
        EditorHost.MouseMove -= OnEditorHostMouseMove;
        EditorHost.MouseLeave -= OnEditorHostMouseLeave;
        TextView textView = _adapter.Editor.TextArea.TextView;
        textView.QueryCursor -= OnTextViewQueryCursor;
        textView.ScrollOffsetChanged -= OnTextViewScrollOffsetChanged;
        textView.VisualLinesChanged -= OnTextViewVisualLinesChanged;
        _adapter.Dispose();
    }

    private void LoadExisting(string? markdown, bool focus)
    {
        ThrowIfDisposed();
        _autoSave.Cancel();
        IsNewMemo = false;
        _editSession.Begin(markdown);
        _adapter.SetMarkdown(_editSession.Snapshot, clearUndo: true, preserveViewState: false);
        UpdateActions();
        UpdateWatermark();
        HideStatus();
        if (focus)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (Volatile.Read(ref _disposed) == 0 && IsLoaded)
                {
                    FocusEditor();
                }
            }));
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AutomationProperties.SetName(_adapter.View, "所见即所得 Markdown 编辑器");
        AttachOwnerWindow(Window.GetWindow(this));
        _toolbarController.UpdateResponsiveItems();
        UpdateActions();
        UpdateWatermark();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _autoSave.Cancel();
        _statusTimer.Stop();
        _toolbarController.CancelClose();
        PopupAnimations.Close(TablePickerPopup, immediate: true, restoreFocus: false);
        DetachOwnerWindow();
    }

    private void AttachOwnerWindow(Window? window)
    {
        if (ReferenceEquals(window, _ownerWindow))
        {
            return;
        }
        DetachOwnerWindow();
        _ownerWindow = window;
        if (_ownerWindow is not null)
        {
            _ownerWindow.Closed += OnOwnerWindowClosed;
        }
    }

    private void DetachOwnerWindow()
    {
        if (_ownerWindow is not null)
        {
            _ownerWindow.Closed -= OnOwnerWindowClosed;
            _ownerWindow = null;
        }
    }

    private void OnOwnerWindowClosed(object? sender, EventArgs e) => Dispose();

    private void OnAdapterMarkdownChanged(object? sender, EventArgs e)
    {
        UpdateWatermark();
        DraftChanged?.Invoke(Markdown);
        ShowStatus("未保存", isError: false, autoHide: false);
        if (!IsNewMemo)
        {
            _autoSave.Schedule(() => RequestSaveAsync(completeEditing: false));
        }
    }

    private async void OnAdapterSaveRequested(object? sender, EventArgs e) =>
        await RequestSaveAsync(completeEditing: true);

    private async void OnAdapterCancelRequested(object? sender, EventArgs e) =>
        await CancelEditingAsync();

    private void OnAdapterLinkEditRequested(object? sender, EventArgs e) => EditLink();

    private async void OnAdapterPasteImagesRequested(object? sender, EventArgs e) =>
        await InsertClipboardImagesAsync();

    private void OnEditorPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
            !_adapter.TryGetVisibleOffset(e.GetPosition(_adapter.Editor), out int offset))
        {
            return;
        }
        if (TryOpenLinkAtVisibleOffset(offset))
        {
            e.Handled = true;
        }
    }

    private void OnTextViewQueryCursor(object sender, QueryCursorEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
            !_adapter.TryGetVisibleOffset(e.GetPosition(_adapter.Editor), out int offset))
        {
            return;
        }
        if (_adapter.Model.VisualAt(offset, MarkdownVisualKind.Link) is { LinkTarget: not null })
        {
            e.Handled = true;
            e.Cursor = Cursors.Hand;
        }
    }

    private void OnEditorHostMouseMove(object sender, MouseEventArgs e)
    {
        TextView textView = _adapter.Editor.TextArea.TextView;
        if (!textView.IsLoaded || !EditorHost.IsLoaded || !textView.VisualLinesValid)
        {
            HideCodeCopyButton();
            return;
        }

        _hoveredCodeBlock = _adapter.CodeBlockRenderer.HitTest(textView, e.GetPosition(textView));
        UpdateCodeCopyButtonLayout();
    }

    private void OnEditorHostMouseLeave(object sender, MouseEventArgs e) =>
        HideCodeCopyButton();

    private void OnTextViewScrollOffsetChanged(object? sender, EventArgs e) =>
        UpdateCodeCopyButtonLayout();

    private void OnTextViewVisualLinesChanged(object? sender, EventArgs e) =>
        UpdateCodeCopyButtonLayout();

    private void UpdateCodeCopyButtonLayout()
    {
        TextView textView = _adapter.Editor.TextArea.TextView;
        if (_hoveredCodeBlock is not { } span ||
            !textView.IsLoaded ||
            !EditorHost.IsLoaded ||
            !_adapter.CodeBlockRenderer.TryGetCardBounds(textView, span, out Rect cardBounds))
        {
            HideCodeCopyButton();
            return;
        }

        Point position = textView.TranslatePoint(cardBounds.TopLeft, EditorHost);
        double width = CodeCopyButton.ActualWidth > 0 ? CodeCopyButton.ActualWidth : 22;
        double height = CodeCopyButton.ActualHeight > 0 ? CodeCopyButton.ActualHeight : 22;
        CodeCopyButton.Margin = new Thickness(
            position.X + Math.Max(0, cardBounds.Width - width - MarkdownCodeBlockStyle.ButtonInset),
            position.Y + Math.Max(0, cardBounds.Height - height - MarkdownCodeBlockStyle.ButtonInset),
            0,
            0);
        CodeCopyButton.Visibility = Visibility.Visible;
    }

    private void HideCodeCopyButton()
    {
        _hoveredCodeBlock = null;
        CodeCopyButton.Visibility = Visibility.Collapsed;
    }

    private async void OnCodeCopyClick(object sender, RoutedEventArgs e)
    {
        if (_hoveredCodeBlock is not { } span || span.CodeContent is not { } content)
        {
            return;
        }
        try
        {
            if (CodeClipboardWriter is { } writer)
            {
                await writer(content);
            }
            else
            {
                System.Windows.Clipboard.SetText(content);
            }
            ShowStatus("已复制代码", isError: false, autoHide: true);
        }
        catch
        {
            ShowStatus("复制失败", isError: true, autoHide: true);
        }
    }

    private async Task<bool> RequestSaveAsync(bool completeEditing)
    {
        _autoSave.Cancel();
        if (_saveInProgress)
        {
            return false;
        }
        string markdown = Markdown;
        if (IsNewMemo && !MarkdownFormatter.HasMeaningfulContent(markdown))
        {
            ShowStatus("内容不能为空", isError: true, autoHide: false);
            return false;
        }
        if (SaveRequestedAsync is not { } save)
        {
            return false;
        }

        _saveInProgress = true;
        ShowStatus("保存中", isError: false, autoHide: false);
        bool wasNew = IsNewMemo;
        try
        {
            bool saved = await save(new MarkdownSaveRequest(markdown, completeEditing, wasNew));
            if (!saved)
            {
                ShowStatus("保存失败", isError: true, autoHide: false);
                return false;
            }
            if (completeEditing)
            {
                _editSession.Commit(markdown);
            }
            ShowStatus("已保存", isError: false, autoHide: true);
            if (wasNew)
            {
                BeginNew();
            }
            else if (completeEditing)
            {
                EditingCompleted?.Invoke(this, EventArgs.Empty);
            }
            return true;
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, isError: true, autoHide: false);
            return false;
        }
        finally
        {
            _saveInProgress = false;
        }
    }

    private void OnToolbarClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string command })
        {
            return;
        }
        InvokeToolbarCommand(command);
    }

    private void OnMenuCommandClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string command })
        {
            return;
        }
        ContextMenuAnimations.Close(MoreMenu);
        ContextMenuAnimations.Close(HeadingMenu);
        InvokeToolbarCommand(command);
    }

    private void InvokeToolbarCommand(string command)
    {
        ToolbarCommandInvoked?.Invoke(this, new MarkdownToolbarCommandEventArgs(command));
        switch (command)
        {
            case "Save":
                RequestSaveAsync(completeEditing: true).Observe();
                break;
            case "New":
                NewRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "Link":
                EditLink();
                break;
            case "LocalImage":
                SelectLocalImagesAsync().Observe();
                break;
            case "RemoteImage":
                EditRemoteImage();
                break;
            case "EditSource":
                EditSource();
                break;
            case "Table":
                OpenTablePicker(TableButton);
                break;
            default:
                if (Enum.TryParse(command, out MarkdownFormatCommand format))
                {
                    _adapter.Execute(format);
                    RestoreEditorFocus();
                }
                break;
        }
    }

    private void OnHeadingClick(object sender, RoutedEventArgs e) =>
        _toolbarController.ToggleMenu(HeadingButton);

    private void OnMoreClick(object sender, RoutedEventArgs e) =>
        _toolbarController.ToggleMenu(MoreButton);

    private void OnToolbarMenuAnchorMouseLeave(object sender, MouseEventArgs e) =>
        _toolbarController.ScheduleClose();

    private void OnToolbarMenuMouseEnter(object sender, MouseEventArgs e) =>
        _toolbarController.CancelClose();

    private void OnToolbarMenuMouseLeave(object sender, MouseEventArgs e) =>
        _toolbarController.ScheduleClose();

    private void OnToolbarMenuOpened(object sender, RoutedEventArgs e) =>
        _toolbarController.CancelClose();

    private void OnToolbarMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            _toolbarController.NotifyMenuClosed(menu);
        }
        RestoreEditorFocus();
    }

    private void OnTableClick(object sender, RoutedEventArgs e) => OpenTablePicker(TableButton);

    private void OpenTablePicker(Button anchor)
    {
        ContextMenuAnimations.Close(MoreMenu);
        TablePickerPopup.PlacementTarget = anchor;
        TableSizeLabel.Text = "表格";
        PopupAnimations.Open(TablePickerPopup, anchor);
    }

    private void BuildTableSizePicker()
    {
        for (int row = 1; row <= 9; row++)
        {
            for (int column = 1; column <= 9; column++)
            {
                Button cell = new()
                {
                    Width = 19,
                    Height = 19,
                    Margin = new Thickness(1),
                    Padding = new Thickness(0),
                    Tag = (Columns: column, Rows: row),
                    // 不在此处快照画刷：格子创建于设置加载完成之前，若启动主题与
                    // 最终主题不同，快照会残留旧颜色（表现为格子变黑）。默认态交给
                    // MemoBaseButtonStyle 的 DynamicResource 随主题实时更新。
                    Focusable = false,
                    Cursor = Cursors.Hand
                };
                CornerRadiusProxy.SetValue(cell, new CornerRadius(3));
                InteractionAnimations.SetProfile(cell, InteractionAnimationProfile.Button);
                InteractionAnimations.SetHoverScale(cell, 1.06);
                InteractionAnimations.SetPressedScale(cell, 0.94);
                cell.MouseEnter += OnTablePickerCellMouseEnter;
                cell.Click += OnTablePickerCellClick;
                TableSizeGrid.Children.Add(cell);
            }
        }
    }

    private void OnTablePickerCellMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Button { Tag: ValueTuple<int, int> size })
        {
            return;
        }
        TableSizeLabel.Text = $"{size.Item1} × {size.Item2} 表格";
        foreach (Button cell in TableSizeGrid.Children.OfType<Button>())
        {
            (int columns, int rows) = ((int, int))cell.Tag;
            if (columns <= size.Item1 && rows <= size.Item2)
            {
                cell.Background = FindBrush("AccentSubtleBrush", Brushes.MistyRose);
            }
            else
            {
                cell.ClearValue(BackgroundProperty);
            }
        }
    }

    private void OnTablePickerCellClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ValueTuple<int, int> size })
        {
            return;
        }
        MarkdownEditResult result = MarkdownFormatter.InsertTable(
            Markdown,
            _adapter.SelectionStart,
            _adapter.SelectionEnd,
            size.Item1,
            size.Item2);
        _adapter.ApplyEdit(result);
        PopupAnimations.Close(TablePickerPopup);
        RestoreEditorFocus();
    }

    private void OnTablePickerMouseLeave(object sender, MouseEventArgs e)
    {
        TableSizeLabel.Text = "表格";
        ResetTablePickerCellBackgrounds();
    }

    private void OnTablePickerClosed(object sender, EventArgs e)
    {
        // Esc 关闭时指针可能仍悬停在格子上，MouseLeave 不一定触发；
        // 关闭后统一清除本地背景，避免下次打开残留高亮或过期画刷。
        ResetTablePickerCellBackgrounds();
        RestoreEditorFocus();
    }

    private void ResetTablePickerCellBackgrounds()
    {
        foreach (Button cell in TableSizeGrid.Children.OfType<Button>())
        {
            cell.ClearValue(BackgroundProperty);
        }
    }

    private void EditLink()
    {
        Window? owner = Window.GetWindow(this);
        if (owner is null)
        {
            return;
        }
        int sourceStart = _adapter.SelectionStart;
        int sourceEnd = _adapter.SelectionEnd;
        string label = sourceEnd > sourceStart ? Markdown[sourceStart..sourceEnd] : "链接文本";
        string url = string.Empty;
        int replaceStart = sourceStart;
        int replaceEnd = sourceEnd;
        MarkdownVisualSpan? span = _adapter.Model.VisualAt(_adapter.Editor.CaretOffset, MarkdownVisualKind.Link);
        if (span is { LinkTarget: { } target } link)
        {
            label = _adapter.VisibleText.Substring(link.Start, link.Length);
            url = target;
            replaceStart = Markdown.LastIndexOf('[', link.SourceStart);
            int close = Markdown.IndexOf(')', link.SourceStart + link.SourceLength);
            if (replaceStart < 0 || close < replaceStart)
            {
                replaceStart = sourceStart;
                replaceEnd = sourceEnd;
            }
            else
            {
                replaceEnd = close + 1;
            }
        }
        LinkEditValue? value = new LinkEditDialog(label, url).ShowDialog(owner);
        if (value is null)
        {
            RestoreEditorFocus(sourceStart, sourceEnd);
            return;
        }
        string safeLabel = value.Label.Replace("]", "\\]", StringComparison.Ordinal);
        string replacement = $"[{safeLabel}]({value.Url})";
        string result = Markdown[..replaceStart] + replacement + Markdown[replaceEnd..];
        _adapter.ApplyEdit(new MarkdownEditResult(
            result,
            replaceStart + 1,
            replaceStart + 1 + safeLabel.Length));
        RestoreEditorFocus();
    }

    private void EditRemoteImage()
    {
        Window? owner = Window.GetWindow(this);
        if (owner is null)
        {
            return;
        }
        int selectionStart = _adapter.SelectionStart;
        int selectionEnd = _adapter.SelectionEnd;
        string? url = new ImageUrlDialog().ShowDialog(owner);
        if (url is null)
        {
            RestoreEditorFocus(selectionStart, selectionEnd);
            return;
        }
        _adapter.ApplyEdit(MarkdownFormatter.InsertImage(
            Markdown, selectionStart, selectionEnd, "网络图片", url));
        RestoreEditorFocus();
    }

    private void EditSource()
    {
        Window? owner = Window.GetWindow(this);
        if (owner is null)
        {
            return;
        }
        int selectionStart = _adapter.SelectionStart;
        int selectionEnd = _adapter.SelectionEnd;
        string? value = new MarkdownSourceDialog(Markdown).ShowDialog(owner);
        if (value is null || value == Markdown)
        {
            RestoreEditorFocus(selectionStart, selectionEnd);
            return;
        }
        _adapter.ApplyEdit(new MarkdownEditResult(value, 0, 0));
        RestoreEditorFocus();
    }

    internal bool TryOpenLinkAtVisibleOffset(int offset)
    {
        MarkdownVisualSpan? span = _adapter.Model.VisualAt(offset, MarkdownVisualKind.Link);
        if (span is not { LinkTarget: { } target })
        {
            return false;
        }
        SafeHyperlinkCommand command = new(LinkLauncher);
        if (!command.CanExecute(target))
        {
            return false;
        }
        command.Execute(target);
        return true;
    }

    private async Task SelectLocalImagesAsync()
    {
        OpenFileDialog dialog = new()
        {
            Title = "插入图片",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.svg"
        };
        bool? accepted = dialog.ShowDialog(Window.GetWindow(this));
        if (accepted == true)
        {
            await InsertFilePathsAsync(dialog.FileNames);
        }
        else
        {
            RestoreEditorFocus();
        }
    }

    private async Task InsertClipboardImagesAsync()
    {
        try
        {
            if (WinFormsClipboard.ContainsFileDropList())
            {
                StringCollection paths = WinFormsClipboard.GetFileDropList();
                await InsertFilePathsAsync(paths.Cast<string>());
                return;
            }
            using System.Drawing.Image? image = WinFormsClipboard.GetImage();
            if (image is null)
            {
                return;
            }
            await using MemoryStream stream = new();
            image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            string stored = await _imageStore.StoreAsync(stream, ".png");
            InsertStoredImages([("粘贴的图片", stored)]);
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, isError: true, autoHide: false);
        }
    }

    private async Task InsertFilePathsAsync(IEnumerable<string> files)
    {
        string[] paths = files.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        if (!ValidateImageFiles(paths, out string? error))
        {
            if (error is not null)
            {
                ShowStatus(error, isError: true, autoHide: false);
            }
            return;
        }

        try
        {
            List<(string Alt, string Uri)> stored = [];
            foreach (string path in paths)
            {
                string uri = await _imageStore.StoreFileAsync(path);
                stored.Add((Path.GetFileNameWithoutExtension(path), uri));
            }
            InsertStoredImages(stored);
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, isError: true, autoHide: false);
        }
    }

    private void InsertStoredImages(IReadOnlyList<(string Alt, string Uri)> images)
    {
        string markdown = Markdown;
        int start = _adapter.SelectionStart;
        int end = _adapter.SelectionEnd;
        MarkdownEditResult result = new(markdown, start, end);
        foreach ((string alt, string uri) in images)
        {
            result = MarkdownFormatter.InsertImage(
                result.Text, result.SelectionStart, result.SelectionEnd, alt, uri);
        }
        _adapter.ApplyEdit(result);
        RestoreEditorFocus();
    }

    private static bool ValidateImageFiles(IReadOnlyCollection<string> files, out string? error)
    {
        error = null;
        if (files.Count == 0)
        {
            return false;
        }
        foreach (string file in files)
        {
            if (!File.Exists(file) || !MarkdownImageStore.IsSupportedFile(file))
            {
                error = "仅支持 png、jpg、jpeg、webp、gif、bmp 或 svg 图片。";
                return false;
            }
            if (new FileInfo(file).Length > MarkdownImageStore.MaximumImageBytes)
            {
                error = "图片不能超过 20 MB。";
                return false;
            }
        }
        return true;
    }

    private void OnDragEnter(object sender, DragEventArgs e) => UpdateDragEffects(e);
    private void OnDragOver(object sender, DragEventArgs e) => UpdateDragEffects(e);

    private void UpdateDragEffects(DragEventArgs e)
    {
        string[] files = GetDroppedFiles(e.Data);
        bool accepted = !_dropInProgress && ValidateImageFiles(files, out _);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        DropCue.Opacity = accepted ? 1 : 0;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropCue.Opacity = 0;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        string[] files = GetDroppedFiles(e.Data);
        bool accepted = ValidateImageFiles(files, out _);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        DropCue.Opacity = 0;
        if (!accepted || _dropInProgress)
        {
            return;
        }
        _dropInProgress = true;
        try
        {
            await InsertFilePathsAsync(files);
        }
        finally
        {
            _dropInProgress = false;
        }
    }

    private static string[] GetDroppedFiles(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
            ? files
            : [];

    private void RestoreEditorFocus(int? selectionStart = null, int? selectionEnd = null)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            if (selectionStart is { } start)
            {
                _adapter.SelectSourceRange(start, selectionEnd ?? start);
            }
            _adapter.FocusEditor();
        }));
    }

    private void UpdateActions()
    {
        NewButton.Visibility = !IsNewMemo && ShowNewAction ? Visibility.Visible : Visibility.Collapsed;
        Toolbar.Visibility = HideToolbarInPreview ? Visibility.Collapsed : Visibility.Visible;
        UpdateChrome();
    }

    private void UpdateWatermark() =>
        WatermarkText.Visibility = _adapter.VisibleText.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateChrome()
    {
        Surface.BorderThickness = UseBorderlessChrome ? new Thickness(0) : new Thickness(1);
        Surface.CornerRadius = UseBorderlessChrome ? new CornerRadius(0) : new CornerRadius(10);
        Toolbar.CornerRadius = UseBorderlessChrome ? new CornerRadius(0) : new CornerRadius(9, 9, 0, 0);
        if (UseBorderlessChrome)
        {
            Surface.SetResourceReference(Border.BackgroundProperty, "TransparentBrush");
            Toolbar.SetResourceReference(Border.BackgroundProperty, "TransparentBrush");
        }
        else
        {
            Surface.SetResourceReference(
                Border.BackgroundProperty,
                BlendBordersInPreview ? "BgPrimaryBrush" : "SurfacePrimaryBrush");
            Toolbar.SetResourceReference(Border.BackgroundProperty, "BgSecondaryBrush");
        }
    }

    private void ShowStatus(string message, bool isError, bool autoHide)
    {
        _statusTimer.Stop();
        StatusText.Text = message;
        StatusText.Foreground = FindBrush(isError ? "DangerPrimaryBrush" : "TextSecondaryBrush", Brushes.DimGray);
        StatusBadge.Background = FindBrush(isError ? "DangerSubtleBrush" : "BgTertiaryBrush", Brushes.WhiteSmoke);
        StatusBadge.Opacity = 1;
        if (autoHide)
        {
            _statusTimer.Start();
        }
    }

    private void HideStatus()
    {
        _statusTimer.Stop();
        StatusBadge.Opacity = 0;
    }

    private void OnStatusTimerTick(object? sender, EventArgs e) => HideStatus();

    private static Brush FindBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private static void OnChromeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MarkdownEditor)d).UpdateChrome();

    private static void OnToolbarVisibilityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MarkdownEditor)d).UpdateActions();

    private static void OnShowNewActionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MarkdownEditor)d).UpdateActions();

    private static void OnBlendBordersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MarkdownEditor)d).UpdateChrome();

    private static void OnWatermarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MarkdownEditor)d).WatermarkText.Text = e.NewValue as string ?? string.Empty;

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Utils;
using MemeMomo.Markdown;
using MemeMomo.Services;
using MemeMomo.UI;
using MemeMomo.UI.Popup;
using Application = System.Windows.Application;
using DataObject = System.Windows.DataObject;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Stretch = System.Windows.Media.Stretch;
using Point = System.Windows.Point;
using WpfControl = System.Windows.Controls.Control;
using SystemColors = System.Windows.SystemColors;

namespace MemeMomo.Editor;

internal sealed class AvalonEditMarkdownAdapter : IMarkdownEditorAdapter
{
    private readonly MarkdownDocumentModel _model = new();
    private readonly MarkdownRuleInteraction _ruleInteraction;
    private readonly MarkdownObjectElementGenerator _objectGenerator;
    private readonly MarkdownQuotePaddingGenerator _quotePaddingGenerator;
    private readonly MarkdownQuoteRenderer _quoteRenderer;
    private readonly MarkdownRuleRenderer _ruleRenderer;
    private readonly MarkdownCodeBlockPaddingGenerator _codeBlockPaddingGenerator;
    private readonly MarkdownCodeBlockRenderer _codeBlockRenderer;
    private readonly MarkdownInlineCodePaddingGenerator _inlineCodePaddingGenerator;
    private readonly MarkdownInlineCodeRenderer _inlineCodeRenderer;
    private readonly MarkdownSelectionRenderer _selectionRenderer;
    private readonly MarkdownColorizer _colorizer;
    private readonly MarkdownImageLoader _imageLoader;
    private readonly string _imageRoot;
    private readonly MarkdownInputController _inputController;
    private bool _updatingProjection;
    private bool _projectionRefreshPending;
    private int _pendingSelectionStart;
    private int _pendingSelectionEnd;
    private System.Windows.Media.Brush? _normalCaretBrush;
    private bool _redirectingAtomicCaret;
    private long _projectionVersion;
    private readonly Dictionary<int, InlineObjectElement> _tableElements = new();
    private int _disposed;

    internal AvalonEditMarkdownAdapter(string? imageRoot = null, HttpMessageHandler? imageHttpHandler = null)
    {
        _imageRoot = imageRoot ?? MemoDataPaths.RootDirectory;
        Directory.CreateDirectory(_imageRoot);
        _imageLoader = new MarkdownImageLoader(_imageRoot, imageHttpHandler);
        Editor = new TextEditor
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            FontSize = 14,
            Padding = new Thickness(12, 10, 12, 10),
            ShowLineNumbers = false,
            WordWrap = true,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
        };
        Editor.Options.EnableImeSupport = true;
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.SetResourceReference(WpfControl.ForegroundProperty, "TextPrimaryBrush");
        // Inline elements can transiently measure wider than the text column (e.g. a
        // cached image sized before the auto scrollbar reserved its strip); clip them so
        // nothing ever paints over the scrollbar or the surface border.
        Editor.TextArea.TextView.ClipToBounds = true;
        _normalCaretBrush = Editor.TextArea.Caret.CaretBrush;
        MemeMomo.UI.Text.LocalizeExtension.Set(Editor, AutomationProperties.NameProperty, "Markdown 编辑器");

        _quotePaddingGenerator = new MarkdownQuotePaddingGenerator(
            () => _model.Spans,
            Editor.TextArea.TextView);
        Editor.TextArea.TextView.ElementGenerators.Add(_quotePaddingGenerator);
        _ruleInteraction = new MarkdownRuleInteraction(Editor, () => _model);
        _codeBlockPaddingGenerator = new MarkdownCodeBlockPaddingGenerator(_model, Editor.TextArea.TextView);
        Editor.TextArea.TextView.ElementGenerators.Add(_codeBlockPaddingGenerator);
        _inlineCodePaddingGenerator = new MarkdownInlineCodePaddingGenerator(_model);
        Editor.TextArea.TextView.ElementGenerators.Add(_inlineCodePaddingGenerator);
        _objectGenerator = new MarkdownElementGenerator(() => _model.Spans, CreateElement);
        Editor.TextArea.TextView.ElementGenerators.Add(_objectGenerator);
        _quoteRenderer = new MarkdownQuoteRenderer(() => _model.Spans);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_quoteRenderer);
        _ruleRenderer = new MarkdownRuleRenderer(_model, _ruleInteraction);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_ruleRenderer);
        _codeBlockRenderer = new MarkdownCodeBlockRenderer(_model);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_codeBlockRenderer);
        _inlineCodeRenderer = new MarkdownInlineCodeRenderer(_model);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_inlineCodeRenderer);
        Editor.TextArea.SetResourceReference(
            ICSharpCode.AvalonEdit.Editing.TextArea.SelectionForegroundProperty,
            "TextPrimaryBrush");
        Editor.TextArea.SelectionBrush = Brushes.Transparent;
        Editor.TextArea.SelectionBorder = null;
        _selectionRenderer = new MarkdownSelectionRenderer(
            Editor.TextArea,
            _model,
            () => Application.Current?.TryFindResource("TextSelectionBrush") as System.Windows.Media.Brush
                ?? (Editor.TextArea.SelectionBrush is SolidColorBrush { Color.A: > 0 } currentSelection
                    ? currentSelection
                    : SystemColors.HighlightBrush),
            () => Application.Current?.TryFindResource("MarkdownInlineCodeSelectionBrush") as System.Windows.Media.Brush
                ?? Application.Current?.TryFindResource("AccentPressedBrush") as System.Windows.Media.Brush
                ?? SystemColors.HighlightBrush);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_selectionRenderer);
        _colorizer = new MarkdownColorizer(() => _model.Spans);
        Editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        Editor.Document.Changed += OnDocumentChanged;
        Editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        Editor.TextArea.PreviewMouseLeftButtonDown += OnTextAreaPreviewMouseLeftButtonDown;
        Editor.TextArea.PreviewTextInput += OnTextAreaPreviewTextInput;
        Editor.Unloaded += OnUnloaded;
        _inputController = new MarkdownInputController(
            Editor,
            () => _model,
            _ruleInteraction,
            ApplyEdit,
            Execute,
            () => SaveRequested?.Invoke(this, EventArgs.Empty),
            () => CancelRequested?.Invoke(this, EventArgs.Empty),
            () => LinkEditRequested?.Invoke(this, EventArgs.Empty),
            () => PasteImagesRequested?.Invoke(this, EventArgs.Empty));
        Editor.TextArea.ContextMenu = EditorClipboardMenu.CreateForTextArea(
            Editor.TextArea,
            () => PasteImagesRequested?.Invoke(this, EventArgs.Empty));
        Editor.TextArea.MouseRightButtonDown += OnTextAreaMouseRightButtonDown;
        Editor.TextArea.AddHandler(DataObject.CopyingEvent, new DataObjectCopyingEventHandler(OnClipboardCopy));
        RefreshProjection(clearUndo: true, selectionStart: 0, selectionEnd: 0);
    }

    internal TextEditor Editor { get; }
    internal MarkdownDocumentModel Model => _model;
    internal MarkdownCodeBlockRenderer CodeBlockRenderer => _codeBlockRenderer;
    internal MarkdownRuleRenderer RuleRenderer => _ruleRenderer;
    internal MarkdownRuleInteraction RuleInteraction => _ruleInteraction;
    internal int ImageLoadRequestCount => _imageLoader.RequestCount;
    internal int SuccessfulImageLoadCount => _imageLoader.SuccessCount;
    internal int FailedImageLoadCount => _imageLoader.FailureCount;
    internal long ProjectionRefreshCount => Volatile.Read(ref _projectionVersion);
    internal long InlineControlCreationCount => _objectGenerator.CreatedElementCount;

    internal bool TryGetVisibleOffset(Point point, out int offset)
    {
        TextViewPosition? position = Editor.GetPositionFromPoint(point);
        if (position is not { } value)
        {
            offset = 0;
            return false;
        }
        offset = Editor.Document.GetOffset(value.Location);
        return true;
    }

    public FrameworkElement View => Editor;

    public string Markdown
    {
        get => _model.Markdown;
        set => SetMarkdown(value, clearUndo: true, preserveViewState: false);
    }

    public string VisibleText => _model.VisibleText;

    public int SelectionStart => _model.SourceOffsetFromVisible(
        Editor.SelectionStart,
        trailingAffinity: true);

    public int SelectionEnd => _model.SourceOffsetFromVisible(
        Editor.SelectionStart + Editor.SelectionLength,
        trailingAffinity: false);

    public int CaretSourceOffset => _model.SourceOffsetForInsertion(Editor.CaretOffset);

    public event EventHandler? MarkdownChanged;

    public event EventHandler? SaveRequested;

    public event EventHandler? CancelRequested;

    public event EventHandler? LinkEditRequested;

    public event EventHandler? PasteImagesRequested;

    public void Execute(MarkdownFormatCommand command)
    {
        ThrowIfDisposed();
        MarkdownEditResult result = MarkdownFormatter.Apply(
            _model.Markdown,
            SelectionStart,
            SelectionEnd,
            command);
        ApplyEdit(result);
    }

    public void ApplyEdit(MarkdownEditResult result, bool clearUndo = false)
    {
        ThrowIfDisposed();
        string beforeMarkdown = _model.Markdown;
        // UndoCaret 覆盖撤销时的光标位置：占位符这类整体结构删除后，撤销要把光标放回
        // 结构所在行的行首，而不是编辑前的内容位置。
        int beforeStart;
        int beforeEnd;
        if (result.UndoCaret is { } undoCaret)
        {
            beforeStart = undoCaret;
            beforeEnd = undoCaret;
        }
        else
        {
            beforeStart = SelectionStart;
            beforeEnd = SelectionEnd;
        }
        _model.SetEditableMarkdown(result.Text);
        int visibleStart = _model.VisibleOffsetFromSource(result.SelectionStart);
        int visibleEnd = _model.VisibleOffsetFromSource(result.SelectionEnd);

        string afterMarkdown = _model.Markdown;
        int afterStart = result.SelectionStart;
        int afterEnd = result.SelectionEnd;
        // Always pair a source change with an explicit restore operation. The projection
        // replace on the undo stack would otherwise rebuild the source from the visible
        // text on undo, writing placeholders (rule spaces, table objects, task marks) back
        // into the Markdown source as literal characters. The pair is pushed inside the
        // projection update's undo group, so one Ctrl+Z reverts both the projection replace
        // and the source change together instead of leaving an orphaned projection step
        // that later undo rounds would replay through the visible-text diff.
        SourceModelUndoOperation? pairedUndo = null;
        if (!clearUndo && beforeMarkdown != afterMarkdown)
        {
            pairedUndo = new SourceModelUndoOperation(
                () => RestoreSourceState(beforeMarkdown, beforeStart, beforeEnd),
                () => RestoreSourceState(afterMarkdown, afterStart, afterEnd));
        }
        RefreshProjection(clearUndo, visibleStart, visibleEnd, pairedUndo);

        if (beforeMarkdown != afterMarkdown)
        {
            MarkdownChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetMarkdown(string? markdown, bool clearUndo, bool preserveViewState)
    {
        ThrowIfDisposed();
        int sourceStart = preserveViewState ? SelectionStart : 0;
        int sourceEnd = preserveViewState ? SelectionEnd : 0;
        _model.SetEditableMarkdown(markdown);
        RefreshProjection(
            clearUndo,
            _model.VisibleOffsetFromSource(sourceStart),
            _model.VisibleOffsetFromSource(sourceEnd));
    }

    public void SelectSourceRange(int start, int end)
    {
        ThrowIfDisposed();
        int visibleStart = _model.VisibleOffsetFromSource(Math.Clamp(start, 0, _model.Markdown.Length));
        int visibleEnd = _model.VisibleOffsetFromSource(Math.Clamp(end, 0, _model.Markdown.Length));
        Editor.Select(Math.Min(visibleStart, visibleEnd), Math.Abs(visibleEnd - visibleStart));
    }

    public void FocusEditor()
    {
        ThrowIfDisposed();
        Editor.Focus();
        Keyboard.Focus(Editor.TextArea);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Editor.Document.Changed -= OnDocumentChanged;
        Editor.TextArea.Caret.PositionChanged -= OnCaretPositionChanged;
        Editor.TextArea.PreviewMouseLeftButtonDown -= OnTextAreaPreviewMouseLeftButtonDown;
        Editor.TextArea.PreviewTextInput -= OnTextAreaPreviewTextInput;
        Editor.TextArea.MouseRightButtonDown -= OnTextAreaMouseRightButtonDown;
        Editor.TextArea.RemoveHandler(DataObject.CopyingEvent, new DataObjectCopyingEventHandler(OnClipboardCopy));
        Editor.TextArea.ContextMenu = null;
        Editor.Unloaded -= OnUnloaded;
        _inputController.Dispose();
        _imageLoader.Dispose();
        Editor.TextArea.TextView.BackgroundRenderers.Remove(_selectionRenderer);
        Editor.TextArea.TextView.BackgroundRenderers.Remove(_inlineCodeRenderer);
        Editor.TextArea.TextView.BackgroundRenderers.Remove(_codeBlockRenderer);
        Editor.TextArea.TextView.BackgroundRenderers.Remove(_quoteRenderer);
        Editor.TextArea.TextView.BackgroundRenderers.Remove(_ruleRenderer);
        Editor.TextArea.TextView.ElementGenerators.Remove(_inlineCodePaddingGenerator);
        Editor.TextArea.TextView.ElementGenerators.Remove(_codeBlockPaddingGenerator);
        Editor.TextArea.TextView.ElementGenerators.Remove(_quotePaddingGenerator);
        Editor.TextArea.TextView.ElementGenerators.Remove(_objectGenerator);
        Editor.TextArea.TextView.LineTransformers.Remove(_colorizer);
    }

    private void RefreshProjection(
        bool clearUndo,
        int selectionStart,
        int selectionEnd,
        IUndoableOperation? pairedUndo = null)
    {
        _projectionVersion++;
        RefreshProjectionCore(clearUndo, selectionStart, selectionEnd, pairedUndo);
    }

    private void RefreshProjectionCore(
        bool clearUndo,
        int selectionStart,
        int selectionEnd,
        IUndoableOperation? pairedUndo = null)
    {
        double verticalOffset = Editor.VerticalOffset;
        double horizontalOffset = Editor.HorizontalOffset;
        (DocumentLine Line, double Bottom)? scrollAnchor = CaptureScrollAnchor();
        _updatingProjection = true;
        try
        {
            using (Editor.Document.RunUpdate())
            {
                ReplaceProjectionText();
                // 与投影替换同组压入，撤销/重做时作为一步整体执行（见 ApplyEdit）。
                if (pairedUndo is not null)
                {
                    Editor.Document.UndoStack.Push(pairedUndo);
                }
            }

            int start = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, Editor.Document.TextLength);
            int length = Math.Clamp(Math.Abs(selectionEnd - selectionStart), 0, Editor.Document.TextLength - start);
            Editor.Select(start, length);
            Editor.CaretOffset = start + length;
            Editor.ScrollToVerticalOffset(verticalOffset);
            Editor.ScrollToHorizontalOffset(horizontalOffset);
            Editor.TextArea.TextView.Redraw();
            RestoreScrollAnchor(scrollAnchor, verticalOffset);
            if (clearUndo)
            {
                Editor.Document.UndoStack.ClearAll();
            }
        }
        finally
        {
            _updatingProjection = false;
        }

        // The projection rebuild can move or invalidate the bordered rule; re-evaluate the
        // atomic caret once the guard flag is released.
        _ruleInteraction.Clear();
        RedirectCaretOutOfRule();
        RedirectCaretOutOfTable();
    }

    /// <summary>
    /// Rewrites the document text to the projection's visible text. Only the middle range
    /// that actually differs — after trimming the common prefix and suffix — is replaced:
    /// a full-document replace makes AvalonEdit's HeightTree drop every measured line
    /// height (removed lines lose their height, inserted lines start at the default), so
    /// the scroll offset restored by the caller — a number in the old heights' coordinate
    /// system — would point at entirely different content. The view then jumps towards
    /// the document end, and every tall line (image) rebuilt while scrolling up
    /// afterwards shifts the remaining content mid-gesture, snapping the view into it.
    /// Keeping unchanged document lines keeps their measured heights, which makes the
    /// offset restore exact; edits that leave the visible text unchanged (image width
    /// suffixes, bold markers, …) skip the replace entirely.
    /// </summary>
    private void ReplaceProjectionText()
    {
        string current = Editor.Document.Text;
        string target = _model.VisibleText;
        int start = 0;
        int sharedLength = Math.Min(current.Length, target.Length);
        while (start < sharedLength && current[start] == target[start])
        {
            start++;
        }
        int endOld = current.Length;
        int endNew = target.Length;
        while (endOld > start && endNew > start && current[endOld - 1] == target[endNew - 1])
        {
            endOld--;
            endNew--;
        }
        if (start == endOld && start == endNew)
        {
            return;
        }
        Editor.Document.Replace(start, endOld - start, target[start..endNew]);
    }

    /// <summary>
    /// Captures the first visible document line together with its bottom edge (in
    /// document coordinates) so the projection refresh can hold the view steady: a
    /// rebuild can change that line's height (e.g. an image resized through a width
    /// option), and without compensation every pixel below it would shift by the height
    /// delta. Returns null while the visual lines are not valid — there is nothing to
    /// anchor to before the first layout pass.
    /// </summary>
    private (DocumentLine Line, double Bottom)? CaptureScrollAnchor()
    {
        TextView textView = Editor.TextArea.TextView;
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0)
        {
            return null;
        }
        VisualLine firstVisible = textView.VisualLines[0];
        return (firstVisible.FirstDocumentLine, firstVisible.VisualTop + firstVisible.Height);
    }

    /// <summary>
    /// Keeps the captured first visible line's bottom edge at its pre-refresh document
    /// position: the offset shifts by exactly the line's height change, so the content
    /// below it stays where it was on screen (a shrinking image keeps its bottom edge —
    /// where the width toolbar sits — glued in place). Without a height change this
    /// degenerates to the plain offset restore already applied above. Rebuilding the
    /// anchor line is safe here because the diff-based replace preserved the HeightTree
    /// heights of the lines above it; a line deleted by the edit falls back to the plain
    /// restore.
    /// </summary>
    private void RestoreScrollAnchor((DocumentLine Line, double Bottom)? anchor, double verticalOffset)
    {
        if (anchor is not { } value || value.Line.IsDeleted)
        {
            return;
        }
        try
        {
            VisualLine rebuilt = Editor.TextArea.TextView.GetOrConstructVisualLine(value.Line);
            Editor.ScrollToVerticalOffset(verticalOffset + rebuilt.VisualTop + rebuilt.Height - value.Bottom);
        }
        catch (InvalidOperationException)
        {
            // The anchor line cannot be rebuilt right now; keep the plain offset restore.
        }
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_updatingProjection || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        int caret = Editor.CaretOffset;
        (int Start, int End) selection = _model.ApplyVisibleText(
            Editor.Text,
            e.Offset,
            e.RemovalLength,
            e.InsertionLength);
        int selectionStart = selection == (0, 0) ? caret : selection.Start;
        int selectionEnd = selection == (0, 0) ? caret : selection.End;
        if (!string.Equals(Editor.Text, _model.VisibleText, StringComparison.Ordinal))
        {
            ScheduleProjectionRefresh(selectionStart, selectionEnd);
        }

        MarkdownChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_redirectingAtomicCaret || _updatingProjection || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // Any caret move clears the bordered rule: the border only represents the caret
        // while the caret itself stays untouched on the neighbouring line.
        _ruleInteraction.Clear();
        if (Editor.SelectionLength != 0)
        {
            return;
        }

        if (RedirectCaretOutOfRule())
        {
            return;
        }

        RedirectCaretOutOfTable();
    }

    /// <summary>
    /// 表格是原子对象：光标不允许停在表格行内（可见范围 [Start, End]），否则下一次
    /// Backspace 会被输入层按"删除原子对象"处理成整表删除。重定向到下一行行首；目标
    /// 必须同时收敛到当前文档长度——投影刷新前文档可能短暂短于 VisibleText（BackSpace
    /// 删掉表格边界换行、Ctrl+Z 的中间态等），超界赋值会让 AvalonEdit 在 GetLocation
    /// 中抛 ArgumentOutOfRangeException 并闪退。焦点在单元格内时跳过：单元格编辑期间
    /// 投影刷新会刻意把文本区光标放回表格内，此时不能打扰单元格输入。
    /// </summary>
    private bool RedirectCaretOutOfTable()
    {
        if (Editor.SelectionLength != 0 || IsAnyTableCellFocused())
        {
            return false;
        }

        int caret = Editor.CaretOffset;
        MarkdownVisualSpan? table = _model.Spans
            .Where(span => span.Kind == MarkdownVisualKind.Table &&
                caret >= span.Start && caret <= span.End)
            .OrderByDescending(span => span.SourceLength)
            .Select(span => (MarkdownVisualSpan?)span)
            .FirstOrDefault();
        if (table is not { } atomicTable)
        {
            return false;
        }

        int target = Math.Min(
            Math.Min(atomicTable.End + 1, _model.VisibleText.Length),
            Editor.Document.TextLength);
        if (target == caret)
        {
            return false;
        }

        _redirectingAtomicCaret = true;
        try
        {
            Editor.CaretOffset = target;
        }
        finally
        {
            _redirectingAtomicCaret = false;
        }
        return true;
    }

    private bool IsAnyTableCellFocused() => _tableElements.Values.Any(element =>
        element.Element is MarkdownTableControl control && control.IsKeyboardFocusWithinTable);

    /// <summary>
    /// The caret never rests on a rule line: redirect it to the start of the line below.
    /// Returns true when the caret was moved.
    /// </summary>
    private bool RedirectCaretOutOfRule()
    {
        if (Editor.SelectionLength != 0)
        {
            return false;
        }

        int caret = Editor.CaretOffset;
        MarkdownVisualSpan? rule = _model.Spans
            .Where(span => span.Kind == MarkdownVisualKind.Rule &&
                caret >= span.Start && caret <= span.End)
            .Select(span => (MarkdownVisualSpan?)span)
            .FirstOrDefault();
        if (rule is not { } atomicRule)
        {
            return false;
        }

        int target = Math.Min(
            Math.Min(atomicRule.End + 1, _model.VisibleText.Length),
            Editor.Document.TextLength);
        if (target == caret)
        {
            return false;
        }

        _redirectingAtomicCaret = true;
        try
        {
            Editor.CaretOffset = target;
        }
        finally
        {
            _redirectingAtomicCaret = false;
        }
        return true;
    }

    private void OnTextAreaPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0 || !Editor.TextArea.TextView.VisualLinesValid)
        {
            return;
        }

        TextView textView = Editor.TextArea.TextView;
        if (_ruleInteraction.HitRule(e.GetPosition(textView)) is { } rule)
        {
            // Clicking a rule selects it with a border instead of placing the caret on the
            // rule line; a double click re-enters the same bordered state.
            Editor.TextArea.Focus();
            _ruleInteraction.TryEnterBorder(rule);
            e.Handled = true;
        }
        else if (_ruleInteraction.IsBordered)
        {
            _ruleInteraction.Clear();
        }
    }

    private void OnTextAreaPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Typing while a rule is bordered starts editing below the rule instead of at the
        // stale caret position.
        if (_ruleInteraction.IsBordered)
        {
            _ruleInteraction.TryExit(RuleExitDirection.Down);
        }
    }

    /// <summary>
    /// 复制/剪切（AvalonEdit 的剪切内部走同一条复制路径）写剪贴板前在此事件里改写文本：
    /// 图片在投影里只是单个 U+FFFC 占位符，不改写就会把"￼"复制出去。占位符按投影 span
    /// 还原成完整图片源码（"![名称](路径)"），其余字符保持原样。空选区复制对应 AvalonEdit
    /// 的 CutCopyWholeLine 整行复制，同样参与还原。表格单元格的复制由 TextBox 自己发起，
    /// 事件只冒泡路过本 TextArea，按 OriginalSource 区分跳过；矩形选择每行一段、文本
    /// 拼接顺序特殊，不做还原。
    /// </summary>
    private void OnClipboardCopy(object sender, DataObjectCopyingEventArgs e)
    {
        if (_updatingProjection || Volatile.Read(ref _disposed) != 0 ||
            !ReferenceEquals(e.OriginalSource, Editor.TextArea))
        {
            return;
        }

        TextArea textArea = Editor.TextArea;
        int visibleStart;
        int visibleEnd;
        if (textArea.Selection.IsEmpty)
        {
            DocumentLine line = textArea.Document.GetLineByNumber(textArea.Caret.Line);
            visibleStart = line.Offset;
            visibleEnd = line.Offset + line.TotalLength;
        }
        else
        {
            SelectionSegment? segment = null;
            foreach (SelectionSegment current in textArea.Selection.Segments)
            {
                if (segment is not null)
                {
                    return;
                }
                segment = current;
            }
            if (segment is not { } value)
            {
                return;
            }
            visibleStart = value.StartOffset;
            visibleEnd = value.EndOffset;
        }

        string? mapped = RestoreImageSources(visibleStart, visibleEnd);
        if (mapped is null || e.DataObject is not DataObject dataObject)
        {
            return;
        }
        dataObject.SetText(mapped);
        dataObject.SetData(typeof(string).FullName!, mapped);
    }

    /// <summary>把 [visibleStart, visibleEnd) 内的图片占位符还原成完整图片源码；无占位符时返回 null。</summary>
    private string? RestoreImageSources(int visibleStart, int visibleEnd)
    {
        string visible = _model.VisibleText;
        int start = Math.Clamp(Math.Min(visibleStart, visibleEnd), 0, visible.Length);
        int end = Math.Clamp(Math.Max(visibleStart, visibleEnd), 0, visible.Length);
        // 占位符恒为单字符，图片 span 互不重叠且按位置有序，逐个消费即可。
        MarkdownVisualSpan[] images = _model.Spans
            .Where(span => span.Kind == MarkdownVisualKind.Image && span.Start < end && span.End > start)
            .OrderBy(span => span.Start)
            .ToArray();
        if (images.Length == 0)
        {
            return null;
        }
        StringBuilder mapped = new(end - start);
        int imageIndex = 0;
        for (int index = start; index < end; index++)
        {
            if (imageIndex < images.Length && images[imageIndex].Start == index)
            {
                mapped.Append(_model.Markdown, images[imageIndex].SourceStart, images[imageIndex].SourceLength);
                imageIndex++;
                continue;
            }
            mapped.Append(visible[index]);
        }
        return TextUtilities.NormalizeNewLines(mapped.ToString(), Environment.NewLine);
    }

    private void OnTextAreaMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            IsInsideTableCell(e.OriginalSource as DependencyObject) ||
            !Editor.TextArea.TextView.VisualLinesValid)
        {
            return;
        }
        TryPlaceCaretForRightClick(e.GetPosition(Editor));
    }

    /// <summary>
    /// 右键与 TextBox 一致地把光标带到点击处，剪切/粘贴就地生效；点击落在既有选区内时
    /// 保留选区以便直接复制。落点在原子对象（表格/分割线）内时由 OnCaretPositionChanged
    /// 的重定向逻辑把光标挪出。返回是否移动了光标。落点紧贴图片（点击图片本体时命中点
    /// 只会映射到占位符前后两个位置）时整体选中图片占位符，剪切/复制经 OnClipboardCopy
    /// 得到完整图片源码。
    /// </summary>
    internal bool TryPlaceCaretForRightClick(Point pointToEditor)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            !Editor.TextArea.TextView.VisualLinesValid ||
            !TryGetVisibleOffset(pointToEditor, out int offset))
        {
            return false;
        }
        int selectionStart = Editor.SelectionStart;
        if (Editor.SelectionLength > 0 &&
            offset > selectionStart && offset < selectionStart + Editor.SelectionLength)
        {
            return false;
        }
        if (_model.Spans
                .Where(span => span.Kind == MarkdownVisualKind.Image &&
                    span.Start <= offset && offset <= span.End)
                .Select(span => (MarkdownVisualSpan?)span)
                .FirstOrDefault() is { } image)
        {
            Editor.Select(image.Start, image.Length);
            return true;
        }
        Editor.Select(offset, 0);
        return true;
    }

    /// <summary>右键落在表格单元格内时不动隐藏编辑器光标：单元格有自己的右键菜单。</summary>
    private static bool IsInsideTableCell(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is MarkdownTableControl)
            {
                return true;
            }
            source = source is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Editor.TextArea.ClearSelection();
        SetTableCellFocusVisual(false);
        Keyboard.ClearFocus();
    }

    internal void ScheduleProjectionRefresh(int selectionStart, int selectionEnd)
    {
        _pendingSelectionStart = selectionStart;
        _pendingSelectionEnd = selectionEnd;
        if (_projectionRefreshPending)
        {
            return;
        }

        _projectionRefreshPending = true;
        Editor.Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() =>
            {
                _projectionRefreshPending = false;
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }
                if (!string.Equals(Editor.Text, _model.VisibleText, StringComparison.Ordinal))
                {
                    RefreshProjectionCore(false, _pendingSelectionStart, _pendingSelectionEnd);
                    return;
                }
                // 文档已经与投影一致（例如撤销组内的源码恢复由配对的投影替换收敛），
                // 只剩把恢复操作记录的光标/选区落到位，否则撤销组内投影替换对光标的
                // 搬动会把光标留在错误位置。
                int start = Math.Clamp(
                    Math.Min(_pendingSelectionStart, _pendingSelectionEnd), 0, Editor.Document.TextLength);
                int length = Math.Clamp(
                    Math.Abs(_pendingSelectionEnd - _pendingSelectionStart), 0, Editor.Document.TextLength - start);
                Editor.Select(start, length);
            }));
    }

    internal void SetTableCellFocusVisual(bool active)
    {
        Editor.TextArea.Caret.CaretBrush = active
            ? System.Windows.Media.Brushes.Transparent
            : _normalCaretBrush;
        if (active)
        {
            Editor.TextArea.Caret.Hide();
        }
        else
        {
            Editor.TextArea.Caret.Show();
        }
    }

    /// <summary>
    /// Moves the editor caret to the nearest non-empty line above or below a table. Empty
    /// separator lines are intentionally skipped so a table at the document edge remains
    /// stationary instead of landing on its structural trailing blank line.
    /// </summary>
    internal bool TryMoveCaretOutOfTable(int tableOrdinal, bool moveUpward)
    {
        MarkdownVisualSpan? table = _model.Spans
            .Where(span => span.Kind == MarkdownVisualKind.Table)
            .OrderBy(span => span.SourceStart)
            .Skip(tableOrdinal)
            .Select(span => (MarkdownVisualSpan?)span)
            .FirstOrDefault();
        if (table is not { } currentTable)
        {
            return false;
        }

        DocumentLine tableLine = Editor.Document.GetLineByOffset(
            Math.Clamp(currentTable.Start, 0, Math.Max(0, Editor.Document.TextLength - 1)));
        DocumentLine? targetLine = moveUpward ? tableLine.PreviousLine : tableLine.NextLine;
        while (targetLine is not null)
        {
            string lineText = Editor.Document.GetText(targetLine.Offset, targetLine.Length);
            if (!string.IsNullOrWhiteSpace(lineText))
            {
                int target = moveUpward ? targetLine.EndOffset : targetLine.Offset;
                _redirectingAtomicCaret = true;
                try
                {
                    FocusEditor();
                    Editor.CaretOffset = Math.Clamp(target, 0, Editor.Document.TextLength);
                    Editor.TextArea.Caret.BringCaretToView();
                }
                finally
                {
                    _redirectingAtomicCaret = false;
                }
                return true;
            }

            targetLine = moveUpward ? targetLine.PreviousLine : targetLine.NextLine;
        }

        return false;
    }

    private UIElement CreateControl(MarkdownVisualSpan span) => span.Kind switch
    {
        MarkdownVisualKind.Task => CreateTaskControl(span),
        _ => new MarkdownImageControl(_imageLoader, Editor.TextArea.TextView, span, this)
    };

    /// <summary>
    /// Creates the inline element for one visual span. Tables are cached per table ordinal:
    /// the same <see cref="InlineObjectElement"/> — and with it the same table control — is
    /// handed back on every visual line rebuild, so a rebuild never removes the focused
    /// cell TextBox from the visual tree and never disturbs an active IME composition.
    /// </summary>
    private VisualLineElement CreateElement(MarkdownVisualSpan span)
    {
        if (span.Kind != MarkdownVisualKind.Table)
        {
            return new InlineObjectElement(span.Length, CreateControl(span));
        }
        int tableCount = _model.Spans.Count(value => value.Kind == MarkdownVisualKind.Table);
        foreach (int stale in _tableElements.Keys.Where(key => key >= tableCount).ToList())
        {
            _tableElements.Remove(stale);
        }
        int tableOrdinal = _model.Spans
            .Where(value => value.Kind == MarkdownVisualKind.Table && value.SourceStart < span.SourceStart)
            .Count();
        if (_tableElements.TryGetValue(tableOrdinal, out InlineObjectElement? cached) &&
            cached.Element is MarkdownTableControl table)
        {
            table.SyncFromSource();
            return cached;
        }
        MarkdownTableControl control = new(this, tableOrdinal);
        InlineObjectElement element = new(span.Length, control);
        _tableElements[tableOrdinal] = element;
        control.SyncFromSource();
        return element;
    }

    private System.Windows.Controls.CheckBox CreateTaskControl(MarkdownVisualSpan span)
    {
        bool syncing = false;
        TextView textView = Editor.TextArea.TextView;
        System.Windows.Controls.CheckBox checkBox = TaskCheckBoxFactory.Create(
            textView.DefaultLineHeight,
            textView.DefaultBaseline);
        checkBox.IsChecked = IsTaskChecked(span);
        MemeMomo.UI.Text.LocalizeExtension.Set(checkBox, AutomationProperties.NameProperty, "任务复选框");
        RoutedEventHandler update = (_, _) =>
        {
            if (syncing || !checkBox.IsLoaded)
            {
                return;
            }
            MarkdownVisualSpan? current = _model.Spans.FirstOrDefault(value =>
                value.Kind == MarkdownVisualKind.Task && value.Start == span.Start);
            if (current is not { } task)
            {
                return;
            }
            string source = _model.Markdown;
            int marker = source.IndexOf('[', task.SourceStart, task.SourceLength);
            if (marker < 0 || marker + 2 >= source.Length)
            {
                return;
            }
            string replacement = checkBox.IsChecked == true ? "x" : " ";
            ApplySourceRange(marker + 1, 1, replacement);
        };
        checkBox.Checked += update;
        checkBox.Unchecked += update;
        checkBox.Unloaded += (_, _) =>
        {
            syncing = true;
            checkBox.Checked -= update;
            checkBox.Unchecked -= update;
        };
        return checkBox;
    }

    private bool IsTaskChecked(MarkdownVisualSpan span)
    {
        int marker = _model.Markdown.IndexOf('[', span.SourceStart, span.SourceLength);
        return marker >= 0 && marker + 2 < _model.Markdown.Length &&
            char.ToLowerInvariant(_model.Markdown[marker + 1]) == 'x';
    }

    private void ApplySourceRange(int start, int length, string replacement)
    {
        string source = _model.Markdown.Remove(start, length).Insert(start, replacement);
        ApplyEdit(new MarkdownEditResult(source, start, start));
    }

    internal sealed class SourceModelUndoOperation(Action undo, Action redo) : IUndoableOperation
    {
        public void Undo() => undo();
        public void Redo() => redo();
    }

    private void RestoreSourceState(string markdown, int selectionStart, int selectionEnd)
    {
        _model.SetEditableMarkdown(markdown);
        int visibleStart = _model.VisibleOffsetFromSource(selectionStart);
        int visibleEnd = _model.VisibleOffsetFromSource(selectionEnd);
        if (!string.Equals(Editor.Text, _model.VisibleText, StringComparison.Ordinal))
        {
            ScheduleProjectionRefresh(visibleStart, visibleEnd);
        }
        else
        {
            Editor.Select(Math.Min(visibleStart, visibleEnd), Math.Abs(visibleEnd - visibleStart));
            Editor.TextArea.TextView.Redraw();
        }
        MarkdownChanged?.Invoke(this, EventArgs.Empty);
    }

    // Entry points for MarkdownTableControl: the control lives in its own file and raises
    // adapter state changes on behalf of the table it renders.
    internal void NotifyMarkdownChanged() => MarkdownChanged?.Invoke(this, EventArgs.Empty);

    internal void NotifySaveRequested() => SaveRequested?.Invoke(this, EventArgs.Empty);

    internal void NotifyCancelRequested() => CancelRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>表格删除确认钩子，转发给输入层；由宿主组件（MarkdownEditor）设置。</summary>
    internal Func<Task<bool>>? TableDeleteConfirmationAsync
    {
        get => _inputController.TableDeleteConfirmationAsync;
        set => _inputController.TableDeleteConfirmationAsync = value;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

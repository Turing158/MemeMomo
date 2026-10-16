using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using MemeMomo.Markdown;
using MemeMomo.UI.Popup;
using Brushes = System.Windows.Media.Brushes;

namespace MemeMomo.Editor;

internal sealed partial class AvalonEditMarkdownAdapter
{
    private TextDocument? _renderedDocument;
    private TextDocument? _sourceDocument;
    private long _sourceModeVersion;
    private (int Start, int End) _renderedSelection;
    private bool _renderedDocumentNeedsRefresh;
    private (int Index, VisualLineElementGenerator Component)[] _suspendedGenerators = [];
    private (int Index, IBackgroundRenderer Component)[] _suspendedRenderers = [];
    private (int Index, IVisualLineTransformer Component)[] _suspendedTransformers = [];

    internal bool IsSourceMode { get; private set; }

    // Formatters normalize Windows line endings before applying their source offsets.
    internal (int Start, int End) FormattingSelection => IsSourceMode
        ? (NormalizedOffset(Markdown, SelectionStart), NormalizedOffset(Markdown, SelectionEnd))
        : (SelectionStart, SelectionEnd);

    private static int NormalizedOffset(string source, int offset) =>
        source[..Math.Clamp(offset, 0, source.Length)].Replace("\r\n", "\n", StringComparison.Ordinal).Length;

    internal void SetSourceMode(bool sourceMode)
    {
        ThrowIfDisposed();
        if (IsReadOnly || IsSourceMode == sourceMode) return;

        // Finish any visible-text diff before exchanging documents. Keeping the rendered
        // document preserves its undo offsets; source typing uses AvalonEdit's native undo.
        if (_projectionRefreshPending)
            RefreshProjectionCore(false, _pendingSelectionStart, _pendingSelectionEnd);
        int start = Editor.SelectionLength == 0 ? CaretSourceOffset : SelectionStart;
        int end = Editor.SelectionLength == 0 ? start : SelectionEnd;
        if (sourceMode && ActiveTableCell is { IsLoaded: true, Tag: MarkdownTableEditor.Cell cellSource } cell)
        {
            start = cellSource.SourceStart + cell.Model.SourceOffsetForInsertion(cell.SelectionStart);
            end = cell.SelectionLength == 0 ? start : cellSource.SourceStart +
                cell.Model.SourceOffsetFromVisible(cell.SelectionStart + cell.SelectionLength, trailingAffinity: false);
        }
        string markdown = Markdown;
        if (!sourceMode)
        {
            start = NormalizedOffset(markdown, start);
            end = NormalizedOffset(markdown, end);
        }
        double verticalOffset = Editor.VerticalOffset;
        _sourceModeVersion++;
        _projectionRefreshPending = false;
        _ruleInteraction.Clear();
        ActiveTableCell = null;
        SetTableCellFocusVisual(false);
        if (Editor.TextArea.ContextMenu is { } menu)
            ContextMenuAnimations.Close(menu, immediate: true);

        _updatingProjection = true;
        try
        {
            IsSourceMode = sourceMode;
            _inputController.IsSourceMode = sourceMode;
            SetMarkdownRendering(!sourceMode);
            if (sourceMode)
            {
                _renderedDocument = Editor.Document;
                _renderedSelection = (start, end);
                _renderedDocumentNeedsRefresh = false;
                if (_sourceDocument?.Text != markdown)
                    _sourceDocument = new TextDocument(markdown);
                SwitchDocument(_sourceDocument);
                SelectSourceRange(start, end);
            }
            else
            {
                SwitchDocument(_renderedDocument!);
                _renderedDocument = null;
                SelectSourceRange(_renderedSelection.Start, _renderedSelection.End);
            }
        }
        finally { _updatingProjection = false; }

        // One rendered undo unit restores the source-edit session without mixing raw
        // source offsets into the projection's undo stack. A view-only toggle adds none.
        if (!sourceMode)
        {
            if (markdown != _model.Markdown || _renderedDocumentNeedsRefresh)
                ApplyEdit(new MarkdownEditResult(markdown, start, end));
            else
                SelectSourceRange(start, end);
            _renderedDocumentNeedsRefresh = false;
        }
        Editor.ScrollToVerticalOffset(verticalOffset);
        Editor.TextArea.Caret.BringCaretToView();
        PrepareContextMenu(null);
    }

    private void SwitchDocument(TextDocument document)
    {
        Editor.Document.Changed -= OnDocumentChanged;
        Editor.Document = document;
        Editor.Document.Changed += OnDocumentChanged;
    }

    private void SetMarkdownRendering(bool enabled)
    {
        TextView view = Editor.TextArea.TextView;
        VisualLineElementGenerator[] generators =
            [_htmlAlignment, _quotePaddingGenerator, _codeBlockPaddingGenerator, _inlineCodePaddingGenerator, _listIndentation, _objectGenerator, _lineSpacing, _paragraphSpacing];
        IBackgroundRenderer[] renderers =
            [_quoteRenderer, _ruleRenderer, _codeBlockRenderer, _inlineCodeRenderer, _selectionRenderer];
        IVisualLineTransformer[] transformers = [_colorizer, _htmlAlignment, _lineSpacing, _paragraphSpacing];
        SetRenderingComponents(view.ElementGenerators, generators, enabled, ref _suspendedGenerators);
        SetRenderingComponents(view.BackgroundRenderers, renderers, enabled, ref _suspendedRenderers);
        SetRenderingComponents(view.LineTransformers, transformers, enabled, ref _suspendedTransformers);
        if (enabled)
            Editor.TextArea.SelectionBrush = Brushes.Transparent;
        else
            Editor.TextArea.SetResourceReference(TextArea.SelectionBrushProperty, "TextSelectionBrush");
        view.Redraw();
    }

    private static void SetRenderingComponents<T>(IList<T> collection, T[] components, bool enabled,
        ref (int Index, T Component)[] suspended)
    {
        if (enabled)
        {
            foreach (var entry in suspended)
                collection.Insert(Math.Min(entry.Index, collection.Count), entry.Component);
            suspended = [];
        }
        else
        {
            suspended = components.Select(component => (Index: collection.IndexOf(component), Component: component))
                .Where(entry => entry.Index >= 0).OrderBy(entry => entry.Index).ToArray();
            foreach (T component in components) collection.Remove(component);
        }
    }

    private void ApplySourceEdit(MarkdownEditResult result, bool clearUndo)
    {
        using (Editor.Document.RunUpdate())
            ReplaceDocumentText(result.Text);
        SelectSourceRange(result.SelectionStart, result.SelectionEnd);
        if (clearUndo) Editor.Document.UndoStack.ClearAll();
    }

    private void SetSourceMarkdown(string? markdown, bool clearUndo, bool preserveViewState)
    {
        if (IsReadOnly)
        {
            IsSourceMode = false;
            _inputController.IsSourceMode = false;
            SwitchDocument(_renderedDocument!);
            _renderedDocument = null;
            _sourceDocument = null;
            SetMarkdownRendering(true);
            SetMarkdown(markdown, clearUndo, preserveViewState: false);
            return;
        }
        int start = preserveViewState ? SelectionStart : 0;
        int end = preserveViewState ? SelectionEnd : 0;
        if (!preserveViewState) ResetInteractionSession();
        _model.SetMarkdown(markdown);
        _renderedDocument = new TextDocument(_model.VisibleText);
        _renderedDocumentNeedsRefresh = true;
        _renderedSelection = (start, end);
        _updatingProjection = true;
        try { ApplySourceEdit(new MarkdownEditResult(_model.Markdown, start, end), clearUndo); }
        finally { _updatingProjection = false; }
    }
}

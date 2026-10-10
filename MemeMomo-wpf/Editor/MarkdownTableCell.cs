using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using MemeMomo.Markdown;

namespace MemeMomo.Editor;

/// <summary>A persistent, marker-free editor for a table cell's inline Markdown.</summary>
internal sealed class MarkdownTableCell : TextEditor
{
    private readonly MarkdownDocumentModel _model = new();
    private bool _updating;
    private bool _refreshPending;
    private int _pendingStart;
    private int _pendingEnd;

    internal MarkdownTableCell()
    {
        WordWrap = true;
        Options.EnableImeSupport = true;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        TextArea.TextView.LineTransformers.Add(new MarkdownVisualTransformer(() => _model.Spans));
        HtmlAlignmentLayout htmlAlignment = new(() => _model.Spans);
        TextArea.TextView.ElementGenerators.Add(htmlAlignment);
        TextArea.TextView.LineTransformers.Add(htmlAlignment);
        TextArea.TextView.SizeChanged += htmlAlignment.OnViewportChanged;
        TextArea.TextView.ElementGenerators.Add(new MarkdownInlineCodePaddingGenerator(_model));
        TextArea.TextView.BackgroundRenderers.Add(new MarkdownInlineCodeRenderer(_model));
        TextArea.TextView.BackgroundRenderers.Add(MarkdownSelectionRenderer.Create(this, _model));
        Document.Changed += OnDocumentChanged;
    }

    internal event EventHandler? MarkdownChanged;

    internal MarkdownDocumentModel Model => _model;

    internal string Markdown
    {
        get => _model.Markdown;
        set
        {
            if (value == _model.Markdown)
                return;
            int start = SelectionStart;
            int end = start + SelectionLength;
            _model.SetMarkdown(value);
            RefreshProjection(start, end);
        }
    }

    internal int CaretIndex { get => CaretOffset; set => CaretOffset = value; }

    internal void ApplyFormat(MarkdownFormatCommand command)
    {
        int start = _model.SourceOffsetFromVisible(SelectionStart, trailingAffinity: true);
        int end = _model.SourceOffsetFromVisible(SelectionStart + SelectionLength, trailingAffinity: false);
        MarkdownEditResult result = MarkdownFormatter.Apply(_model.Markdown, start, end, command);
        ApplyEdit(result);
    }

    internal void ApplyEdit(MarkdownEditResult result)
    {
        if (IsReadOnly) return;
        _model.SetMarkdown(result.Text);
        RefreshProjection(_model.VisibleOffsetFromSource(result.SelectionStart),
            _model.VisibleOffsetFromSource(result.SelectionEnd));
        MarkdownChanged?.Invoke(this, EventArgs.Empty);
    }

    internal bool IsCaretOnBoundaryLine(bool first)
    {
        TextView view = TextArea.TextView;
        view.EnsureVisualLines();
        VisualLine line = view.GetOrConstructVisualLine(Document.GetLineByOffset(CaretOffset));
        int column = line.GetVisualColumn(CaretOffset - line.FirstDocumentLine.Offset);
        var textLine = line.GetTextLine(column);
        return first
            ? line.FirstDocumentLine.LineNumber == 1 && ReferenceEquals(textLine, line.TextLines[0])
            : line.LastDocumentLine.LineNumber == Document.LineCount && ReferenceEquals(textLine, line.TextLines[^1]);
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs args)
    {
        if (_updating || IsReadOnly)
            return;
        (int start, int end) = _model.ApplyVisibleText(Text, args.Offset, args.RemovalLength, args.InsertionLength);
        _pendingStart = start;
        _pendingEnd = end;
        MarkdownChanged?.Invoke(this, EventArgs.Empty);
        // Do not replace the document during its change notification or IME input.
        // Plain input keeps the same document and caret; only newly typed markers
        // require a deferred projection replacement.
        if (_refreshPending)
            return;
        _refreshPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            _refreshPending = false;
            if (Text != _model.VisibleText)
                RefreshProjection(_pendingStart, _pendingEnd);
            else
                TextArea.TextView.Redraw();
        }));
    }

    private void RefreshProjection(int start, int end)
    {
        _updating = true;
        try
        {
            if (Text != _model.VisibleText)
                Document.Replace(0, Document.TextLength, _model.VisibleText);
            start = Math.Clamp(start, 0, Document.TextLength);
            end = Math.Clamp(end, start, Document.TextLength);
            Select(start, end - start);
            TextArea.TextView.Redraw();
        }
        finally
        {
            _updating = false;
        }
    }
}

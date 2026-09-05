using System.Windows;
using MemeMomo.Markdown;

namespace MemeMomo.Editor;

internal interface IMarkdownEditorAdapter : IDisposable
{
    FrameworkElement View { get; }
    string Markdown { get; set; }
    string VisibleText { get; }
    int SelectionStart { get; }
    int SelectionEnd { get; }
    int CaretSourceOffset { get; }
    event EventHandler? MarkdownChanged;
    event EventHandler? SaveRequested;
    event EventHandler? CancelRequested;
    event EventHandler? LinkEditRequested;
    event EventHandler? PasteImagesRequested;
    void Execute(MarkdownFormatCommand command);
    void ApplyEdit(MarkdownEditResult result, bool clearUndo = false);
    void SetMarkdown(string? markdown, bool clearUndo, bool preserveViewState);
    void SelectSourceRange(int start, int end);
    void FocusEditor();
}

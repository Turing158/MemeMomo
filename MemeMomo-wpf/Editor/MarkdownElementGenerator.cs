using System.Windows;
using MemeMomo.Markdown;

namespace MemeMomo.Editor;

/// <summary>
/// Owns AvalonEdit's marker-free inline object boundary. The legacy type remains as a
/// compatibility base for the Plan 03 spike tests.
/// </summary>
internal sealed class MarkdownElementGenerator(
    Func<IReadOnlyList<MarkdownVisualSpan>> spans,
    Func<MarkdownVisualSpan, ICSharpCode.AvalonEdit.Rendering.VisualLineElement>? elementFactory = null)
    : MarkdownObjectElementGenerator(spans, elementFactory);

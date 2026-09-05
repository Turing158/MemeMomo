using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using MemeMomo.Components.Dialogs;
using MemeMomo.Markdown;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MemeMomo.Editor;

internal sealed partial class MarkdownInputController : IDisposable
{
    private readonly TextEditor _editor;
    private readonly Func<MarkdownDocumentModel> _model;
    private readonly MarkdownRuleInteraction _ruleInteraction;
    private readonly Action<MarkdownEditResult, bool> _applyEdit;
    private readonly Action<MarkdownFormatCommand> _execute;
    private readonly Action _save;
    private readonly Action _cancel;
    private readonly Action _editLink;
    private readonly Action _pasteImages;
    private bool _tableDeleteConfirmationPending;
    private int _disposed;

    /// <summary>表格删除确认钩子；未设置时弹出默认确认框。</summary>
    internal Func<Task<bool>>? TableDeleteConfirmationAsync { get; set; }

    internal MarkdownInputController(
        TextEditor editor,
        Func<MarkdownDocumentModel> model,
        MarkdownRuleInteraction ruleInteraction,
        Action<MarkdownEditResult, bool> applyEdit,
        Action<MarkdownFormatCommand> execute,
        Action save,
        Action cancel,
        Action editLink,
        Action pasteImages)
    {
        _editor = editor;
        _model = model;
        _ruleInteraction = ruleInteraction;
        _applyEdit = applyEdit;
        _execute = execute;
        _save = save;
        _cancel = cancel;
        _editLink = editLink;
        _pasteImages = pasteImages;
        _editor.PreviewKeyDown += OnPreviewKeyDown;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _editor.PreviewKeyDown -= OnPreviewKeyDown;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Inline table cells route preview keys through AvalonEdit before reaching their
        // TextBox. Let the focused cell own the key instead of acting on the hidden editor caret.
        if (IsTableCellInputFocused())
        {
            return;
        }

        // Read modifiers from the event device so tests can raise synthetic chords.
        ModifierKeys modifiers = e.KeyboardDevice.Modifiers;
        bool control = modifiers.HasFlag(ModifierKeys.Control);
        if (control && e.Key == Key.B)
        {
            _execute(MarkdownFormatCommand.Bold);
            e.Handled = true;
            return;
        }
        if (control && e.Key == Key.I)
        {
            _execute(MarkdownFormatCommand.Italic);
            e.Handled = true;
            return;
        }
        if (control && e.Key == Key.K)
        {
            _editLink();
            e.Handled = true;
            return;
        }
        if (control && e.Key == Key.Enter)
        {
            _save();
            e.Handled = true;
            return;
        }
        if (control && e.Key == Key.V && EditorClipboardMenu.HasClipboardImageData())
        {
            _pasteImages();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            _cancel();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Tab && TryIndentList(modifiers.HasFlag(ModifierKeys.Shift)))
        {
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Left or Key.Up or Key.Right or Key.Down &&
            TryRuleArrowNavigation(e.Key, modifiers))
        {
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Back || e.Key == Key.Delete) && TryDeleteBorderedRule())
        {
            e.Handled = true;
            return;
        }
        if (TryDeleteRuleAdjacentToCaret(e.Key))
        {
            e.Handled = true;
            return;
        }
        if (control && e.Key == Key.Back && TryDeleteEmptyCodeBlock())
        {
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Back || e.Key == Key.Delete) && TryDeleteListPlaceholder(e.Key))
        {
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Back || e.Key == Key.Delete) && TryDeleteContinuationPlaceholder(e.Key))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Back && TryRemoveEmptyContinuationLine())
        {
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Back || e.Key == Key.Delete) && TableBeforeEmptyTrailingLine() is { } table)
        {
            _ = DeleteTableAfterConfirmationAsync(table);
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Back || e.Key == Key.Delete) && TryDeleteAtomicObject(e.Key))
        {
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Back || e.Key == Key.Delete) && TryDeleteSelectionOverListPlaceholder())
        {
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Back || e.Key == Key.Delete) && TryDeleteSelectionKeepingContinuation())
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && modifiers.HasFlag(ModifierKeys.Shift))
        {
            // Shift+Enter 在列表项上断开列表（下方重头编号）；在列表续行上与 Enter
            // 一致地保留缩进。其余情况交给默认行为插入普通换行。
            if (_editor.SelectionLength == 0 && (TryBreakListAtCaret() || TryEnterOnListLine()))
            {
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.Enter && !modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (TryRuleEnterBelow())
            {
                e.Handled = true;
                return;
            }
            if (TryContinueQuote() || TryEnterOnListLine())
            {
                e.Handled = true;
            }
        }
    }

    private static bool IsTableCellInputFocused()
    {
        DependencyObject? current = Keyboard.FocusedElement as DependencyObject;
        while (current is not null)
        {
            if (current is MarkdownTableControl)
            {
                return true;
            }
            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    /// <summary>
    /// 方向键与分割线的交互：光标不可达分割线，移动到分割线位置时改为显示 1px 边框；
    /// 再次按键则跳出到分割线上下的内容中，若该方向没有内容则保持边框状态。
    /// </summary>
    private bool TryRuleArrowNavigation(Key key, ModifierKeys modifiers)
    {
        if (modifiers != ModifierKeys.None)
        {
            return false;
        }

        if (_ruleInteraction.IsBordered)
        {
            switch (key)
            {
                case Key.Left or Key.Up:
                    _ruleInteraction.TryExit(RuleExitDirection.Up);
                    break;
                case Key.Right or Key.Down:
                    _ruleInteraction.TryExit(RuleExitDirection.Down);
                    break;
                default:
                    return false;
            }
            return true;
        }

        if (_editor.SelectionLength != 0)
        {
            return false;
        }

        MarkdownDocumentModel model = _model();
        int caret = _editor.CaretOffset;
        DocumentLine line = _editor.Document.GetLineByOffset(caret);
        DocumentLine? adjacent;
        switch (key)
        {
            case Key.Left:
                if (caret != line.Offset) return false;
                adjacent = line.PreviousLine;
                break;
            case Key.Right:
                if (caret != line.EndOffset) return false;
                adjacent = line.NextLine;
                break;
            case Key.Up:
                if (!CaretOnEdgeTextLine(textView: _editor.TextArea.TextView, caret, line, first: true)) return false;
                adjacent = line.PreviousLine;
                break;
            case Key.Down:
                if (!CaretOnEdgeTextLine(textView: _editor.TextArea.TextView, caret, line, first: false)) return false;
                adjacent = line.NextLine;
                break;
            default:
                return false;
        }

        if (adjacent is null || !_ruleInteraction.IsRuleLine(adjacent))
        {
            return false;
        }

        MarkdownVisualSpan rule = model.Spans.First(span =>
            span.Kind == MarkdownVisualKind.Rule &&
            span.Start <= adjacent.Offset && span.End >= adjacent.EndOffset);
        _ruleInteraction.TryEnterBorder(rule);
        return true;
    }

    /// <summary>分割线处于边框选中态时，Backspace/Delete 直接删除分割线。</summary>
    private bool TryDeleteBorderedRule()
    {
        if (_ruleInteraction.SelectedRule is not { } rule)
        {
            return false;
        }

        DeleteRule(_model(), rule);
        return true;
    }

    /// <summary>
    /// 光标在分割线下方一行的行首按 Backspace、或在分割线上方一行的行尾按 Delete 时，
    /// 直接删除分割线本身而不是合并换行。例外：分割线下方是空行且空行的下一行有内容时，
    /// Backspace 只删除该空行，下方内容上移一行，光标留在原处（即内容行行首）。
    /// </summary>
    private bool TryDeleteRuleAdjacentToCaret(Key key)
    {
        if (_editor.SelectionLength != 0 || key is not (Key.Back or Key.Delete))
        {
            return false;
        }

        MarkdownDocumentModel model = _model();
        int caret = _editor.CaretOffset;
        MarkdownVisualSpan? rule = model.Spans
            .Where(span => span.Kind == MarkdownVisualKind.Rule &&
                (key == Key.Back ? caret == span.End + 1 : caret + 1 == span.Start))
            .Select(span => (MarkdownVisualSpan?)span)
            .FirstOrDefault();
        if (rule is not { } target)
        {
            return false;
        }

        if (key == Key.Back && TryRemoveBlankLineBelowRule(model, target))
        {
            return true;
        }

        DeleteRule(model, target);
        return true;
    }

    /// <summary>
    /// 分割线下方是空行、且空行的下一行有内容时，只删除这个空行，让内容行贴到分割线下方；
    /// 光标停在原可见位置，也就是内容行行首。空行后面没有内容（文档末尾或仍是空行）时
    /// 返回 false，交回删除分割线的原有流程。
    /// </summary>
    private bool TryRemoveBlankLineBelowRule(MarkdownDocumentModel model, MarkdownVisualSpan rule)
    {
        string visible = model.VisibleText;
        int blank = rule.End + 1;
        if (blank >= visible.Length || visible[blank] != '\n')
        {
            return false;
        }

        int next = blank + 1;
        if (next >= visible.Length || visible[next] == '\n')
        {
            return false;
        }

        string markdown = model.Markdown;
        int lineBreak = model.SourceOffsetFromVisible(blank);
        if (lineBreak >= markdown.Length || markdown[lineBreak] != '\n')
        {
            return false;
        }

        ApplySource(markdown, lineBreak, lineBreak + 1, string.Empty, lineBreak);
        return true;
    }

    /// <summary>分割线处于边框选中态时按 Enter，在分割线下方换行并把光标移到新的一行。</summary>
    private bool TryRuleEnterBelow()
    {
        if (_ruleInteraction.SelectedRule is not { } rule)
        {
            return false;
        }

        MarkdownDocumentModel model = _model();
        int belowStart = rule.End + 1;
        string visible = model.VisibleText;
        bool belowHasContent = belowStart < visible.Length && visible[belowStart] != '\n';
        if (!belowHasContent)
        {
            _ruleInteraction.TryExit(RuleExitDirection.Down);
            return true;
        }

        // Insert at the source start of the line below (before a quote/list marker) so the
        // new empty line stays outside that block.
        int source = model.SourceOffsetFromVisible(belowStart, trailingAffinity: false);
        ApplySource(model.Markdown, source, source, "\n", source);
        return true;
    }

    private void DeleteRule(MarkdownDocumentModel model, MarkdownVisualSpan rule)
    {
        string markdown = model.Markdown;
        int start = rule.SourceStart;
        int end = start + rule.SourceLength;
        // Absorb the rule's line break plus one separator above so the surrounding
        // paragraphs keep a single standard blank-line separation.
        if (start > 0 && markdown[start - 1] == '\n')
        {
            start--;
        }
        if (end < markdown.Length && markdown[end] == '\n')
        {
            end++;
        }
        ApplySource(markdown, start, end, string.Empty, start);
    }

    private static bool CaretOnEdgeTextLine(TextView textView, int caret, DocumentLine line, bool first)
    {
        VisualLine? visualLine = textView.VisualLines.FirstOrDefault(value => value.FirstDocumentLine == line);
        if (visualLine is null || visualLine.TextLines.Count == 0)
        {
            return false;
        }

        if (visualLine.TextLines.Count == 1)
        {
            return true;
        }

        int visualColumn = visualLine.GetVisualColumn(caret - line.Offset);
        TextLine textLine = visualLine.GetTextLine(visualColumn);
        return first
            ? textLine == visualLine.TextLines[0]
            : textLine == visualLine.TextLines[^1];
    }

    private bool TryContinueList()
    {
        MarkdownDocumentModel model = _model();
        string markdown = model.Markdown;
        int source = model.SourceOffsetFromVisible(_editor.CaretOffset);
        int lineStart = source == 0 ? 0 : markdown.LastIndexOf('\n', source - 1) + 1;
        int lineEnd = markdown.IndexOf('\n', source);
        if (lineEnd < 0) lineEnd = markdown.Length;
        Match match = ListItemLine().Match(markdown[lineStart..lineEnd]);
        if (!match.Success)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(match.Groups[4].Value))
        {
            // 规则 3.2：空列表项上再按 Enter，行首的序号/符号占位变成等宽空格，
            // 该行转为上一列表项的换行内容（续行）。
            if (_editor.SelectionLength != 0)
            {
                return false;
            }
            int width = match.Groups[4].Index;
            ApplySource(markdown, lineStart, lineEnd, new string(' ', width), lineStart + width);
            return true;
        }

        string marker = match.Groups[2].Value;
        if (char.IsDigit(marker[0]))
        {
            marker = Regex.Replace(marker, @"\d+", value =>
                (int.Parse(value.Value, System.Globalization.CultureInfo.InvariantCulture) + 1)
                .ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        string prefix = match.Groups[1].Value + marker + " " +
            (match.Groups[3].Success ? "[ ] " : string.Empty);
        ApplySource(markdown, source, source, "\n" + prefix, source + prefix.Length + 1);
        return true;
    }

    /// <summary>
    /// Enter 在列表结构上的分派：空续行恢复编号（规则 3.4）→ 有内容续行沿用缩进（规则 3.3）
    /// → 列表项续接下一个序号，或把空项占位转成续行（规则 3.1/3.2）。
    /// </summary>
    private bool TryEnterOnListLine()
    {
        MarkdownDocumentModel model = _model();
        string markdown = model.Markdown;
        int source = model.SourceOffsetFromVisible(_editor.CaretOffset);
        int lineStart = source == 0 ? 0 : markdown.LastIndexOf('\n', source - 1) + 1;
        int lineEnd = markdown.IndexOf('\n', source);
        if (lineEnd < 0) lineEnd = markdown.Length;
        string line = markdown[lineStart..lineEnd];

        if (_editor.SelectionLength == 0 &&
            line.Length > 0 && line.All(char.IsWhiteSpace) &&
            TryResumeListNumbering(markdown, lineStart, lineEnd))
        {
            return true;
        }

        if (_editor.SelectionLength == 0 &&
            TryContinueIndentedLine(markdown, lineStart, lineEnd, source))
        {
            return true;
        }

        return TryContinueList();
    }

    /// <summary>
    /// 规则 3.4：空续行（只有占位空格、无内容）上按 Enter，退出续行模式，
    /// 该行原位恢复为上方列表项的下一个编号，继续编号列表。
    /// </summary>
    private bool TryResumeListNumbering(string markdown, int lineStart, int lineEnd)
    {
        int cursor = lineStart;
        while (cursor > 0)
        {
            int previousEnd = cursor - 1;
            int previousStart = previousEnd == 0 ? 0 : markdown.LastIndexOf('\n', previousEnd - 1) + 1;
            string previous = markdown[previousStart..previousEnd];
            if (string.IsNullOrWhiteSpace(previous))
            {
                return false;
            }
            Match match = ListItemLine().Match(previous);
            if (!match.Success)
            {
                if (previous[0] is ' ' or '\t')
                {
                    cursor = previousStart;
                    continue;
                }
                return false;
            }

            string marker = match.Groups[2].Value;
            if (char.IsDigit(marker[0]))
            {
                marker = Regex.Replace(marker, @"\d+", value =>
                    (int.Parse(value.Value, System.Globalization.CultureInfo.InvariantCulture) + 1)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            string prefix = match.Groups[1].Value + marker + " " +
                (match.Groups[3].Success ? "[ ] " : string.Empty);
            ApplySource(markdown, lineStart, lineEnd, prefix, lineStart + prefix.Length);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 规则 3.3：有内容的续行上按 Enter，以上一行的位置为基准，新行行首沿用同样的
    /// 占位空格，继续作为上方序号的换行内容。
    /// </summary>
    private bool TryContinueIndentedLine(string markdown, int lineStart, int lineEnd, int source)
    {
        string line = markdown[lineStart..lineEnd];
        if (line.Length == 0 || !char.IsWhiteSpace(line[0]) || string.IsNullOrWhiteSpace(line))
        {
            return false;
        }
        if (ListItemLine().IsMatch(line))
        {
            return false;
        }
        if (lineStart == 0)
        {
            return false;
        }

        int previousEnd = lineStart - 1;
        int previousStart = previousEnd == 0 ? 0 : markdown.LastIndexOf('\n', previousEnd - 1) + 1;
        string previous = markdown[previousStart..previousEnd];
        bool previousContinues = !string.IsNullOrWhiteSpace(previous) &&
            (ListItemLine().IsMatch(previous) || previous[0] is ' ' or '\t');
        if (!previousContinues)
        {
            return false;
        }

        string placeholder = line[..(line.Length - line.TrimStart().Length)];
        ApplySource(markdown, source, source, "\n" + placeholder, source + 1 + placeholder.Length);
        return true;
    }

    /// <summary>
    /// 规则 4：空续行（只有占位空格、无内容）上按 Backspace，占位空格连同换行一起删除，
    /// 光标回到上一行末尾，相当于撤销这次换行。
    /// </summary>
    private bool TryRemoveEmptyContinuationLine()
    {
        if (_editor.SelectionLength != 0)
        {
            return false;
        }
        MarkdownDocumentModel model = _model();
        string markdown = model.Markdown;
        int source = model.SourceOffsetFromVisible(_editor.CaretOffset);
        int lineStart = source == 0 ? 0 : markdown.LastIndexOf('\n', source - 1) + 1;
        int lineEnd = markdown.IndexOf('\n', source);
        if (lineEnd < 0) lineEnd = markdown.Length;
        string line = markdown[lineStart..lineEnd];
        if (line.Length == 0 || line.Any(ch => !char.IsWhiteSpace(ch)))
        {
            return false;
        }
        if (!IsInsideListItemContent(markdown, lineStart))
        {
            return false;
        }

        int removeStart = lineStart > 0 ? lineStart - 1 : lineStart;
        ApplySource(markdown, removeStart, lineEnd, string.Empty, removeStart);
        return true;
    }

    /// <summary>判断 lineStart 上方的上下文是否处于某个列表项的内容中（列表项或其缩进续行）。</summary>
    private static bool IsInsideListItemContent(string markdown, int lineStart)
    {
        int cursor = lineStart;
        while (cursor > 0)
        {
            int previousEnd = cursor - 1;
            int previousStart = previousEnd == 0 ? 0 : markdown.LastIndexOf('\n', previousEnd - 1) + 1;
            string previous = markdown[previousStart..previousEnd];
            if (string.IsNullOrWhiteSpace(previous))
            {
                return false;
            }
            if (ListItemLine().IsMatch(previous))
            {
                return true;
            }
            if (previous[0] is not (' ' or '\t'))
            {
                return false;
            }
            cursor = previousStart;
        }
        return false;
    }

    /// <summary>
    /// 规则 2：Shift+Enter 在列表项上断开列表。光标处产生一个普通行断开（行中余下内容
    /// 按内容缩进移到新行，仍属当前项，其后补空行分隔），下方同一层级的有序列表项作为
    /// 另一个列表重头从 1 编号。
    /// </summary>
    private bool TryBreakListAtCaret()
    {
        if (_editor.SelectionLength != 0)
        {
            return false;
        }
        MarkdownDocumentModel model = _model();
        string markdown = model.Markdown;
        int source = model.SourceOffsetFromVisible(_editor.CaretOffset);
        int lineStart = source == 0 ? 0 : markdown.LastIndexOf('\n', source - 1) + 1;
        int lineEnd = markdown.IndexOf('\n', source);
        if (lineEnd < 0) lineEnd = markdown.Length;
        Match match = ListItemLine().Match(markdown[lineStart..lineEnd]);
        if (!match.Success)
        {
            return false;
        }

        int contentIndent = match.Groups[4].Index;
        string remainder = markdown[source..lineEnd];
        string head;
        int caret;
        if (remainder.AsSpan().Trim().IsEmpty)
        {
            // 光标在内容末尾：换行后与原有分隔符组成空行，列表在此断开
            head = "\n\n";
            caret = source + 1;
        }
        else
        {
            // 余下内容用与内容列等宽的空格缩进（不能用序号文本本身，否则会被当成新的列表项）
            head = "\n" + new string(' ', contentIndent) + remainder + "\n\n";
            caret = source + 1 + contentIndent;
        }

        var tail = new StringBuilder();
        int counter = 1;
        int cursor = lineEnd + 1;
        int replaceStop = markdown.Length;
        char delimiter = match.Groups[2].Value[^1];
        int itemIndent = match.Groups[1].Length;
        while (cursor < markdown.Length)
        {
            int walkEnd = markdown.IndexOf('\n', cursor);
            bool hasBreak = walkEnd >= 0;
            if (!hasBreak) walkEnd = markdown.Length;
            string line = markdown[cursor..walkEnd];
            Match item = ListItemLine().Match(line);
            if (item.Success && item.Groups[1].Length == itemIndent &&
                char.IsDigit(item.Groups[2].Value[0]) && item.Groups[2].Value[^1] == delimiter)
            {
                tail.Append(match.Groups[1].Value)
                    .Append(counter.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(delimiter).Append(' ')
                    .Append(item.Groups[3].Success ? "[ ] " : string.Empty)
                    .Append(item.Groups[4].Value);
                counter++;
            }
            else if (string.IsNullOrWhiteSpace(line) || line[0] is ' ' or '\t')
            {
                tail.Append(line);
            }
            else
            {
                replaceStop = cursor;
                break;
            }
            if (hasBreak) tail.Append('\n');
            cursor = walkEnd + 1;
            replaceStop = hasBreak ? cursor : markdown.Length;
        }

        ApplySource(markdown, source, replaceStop, head + tail.ToString(), caret);
        return true;
    }

    /// <summary>
    /// 规则 5：框选删除覆盖了续行行首占位空格、且该行仍保留并剩有内容时，删除时保住
    /// 占位空格（等价于删除后自动补回），续行结构不被普通编辑悄悄破坏。跨行选区会让
    /// 该行整体并入上一行，占位随整行消失，无需保护。
    /// </summary>
    private bool TryDeleteSelectionKeepingContinuation()
    {
        if (_editor.SelectionLength == 0)
        {
            return false;
        }
        MarkdownDocumentModel model = _model();
        string markdown = model.Markdown;
        (int sourceStart, int sourceEnd) = model.GetSourceDeletionRange(
            _editor.SelectionStart,
            _editor.SelectionStart + _editor.SelectionLength);
        if (sourceEnd <= sourceStart)
        {
            return false;
        }
        int lineStart = sourceStart == 0 ? 0 : markdown.LastIndexOf('\n', sourceStart - 1) + 1;
        int lineEnd = markdown.IndexOf('\n', sourceStart);
        if (lineEnd < 0) lineEnd = markdown.Length;
        if (sourceEnd > lineEnd)
        {
            return false;
        }
        string line = markdown[lineStart..lineEnd];
        if (line.Length == 0 || !char.IsWhiteSpace(line[0]) ||
            string.IsNullOrWhiteSpace(line) || ListItemLine().IsMatch(line))
        {
            return false;
        }
        if (!IsInsideListItemContent(markdown, lineStart))
        {
            return false;
        }
        int placeholderEnd = lineStart + (line.Length - line.TrimStart().Length);
        if (sourceStart > placeholderEnd)
        {
            return false;
        }
        if (sourceEnd <= placeholderEnd)
        {
            // 选区只落在占位空格内：占位按整体保留，删除被吞掉
            ApplySource(markdown, placeholderEnd, placeholderEnd, string.Empty, placeholderEnd);
            return true;
        }
        if (markdown[sourceEnd..lineEnd].Trim().Length == 0)
        {
            return false;
        }
        ApplySource(markdown, placeholderEnd, sourceEnd, string.Empty, placeholderEnd);
        return true;
    }

    private bool TryContinueQuote()
    {
        if (_editor.SelectionLength != 0)
        {
            return false;
        }

        MarkdownDocumentModel model = _model();
        string markdown = model.Markdown;
        int source = model.SourceOffsetFromVisible(_editor.CaretOffset, trailingAffinity: false);
        int lineStart = source == 0 ? 0 : markdown.LastIndexOf('\n', source - 1) + 1;
        int lineEnd = markdown.IndexOf('\n', source);
        if (lineEnd < 0) lineEnd = markdown.Length;
        Match match = QuoteItemLine().Match(markdown[lineStart..lineEnd]);
        if (!match.Success)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(match.Groups[2].Value))
        {
            ApplySource(markdown, lineStart, lineEnd, string.Empty, lineStart);
            return true;
        }

        string prefix = match.Groups[1].Value + "> ";
        ApplySource(markdown, source, source, "\n" + prefix, source + prefix.Length + 1);
        return true;
    }

    /// <summary>
    /// 列表占位符（缩进 + 序号/符号 + 任务框 + 空格）是一个整体：光标落在占位符内或内容
    /// 起点上时，Backspace/Delete 一次性删除整个占位符；空列表项则连同整行删除以退出列表。
    /// 删除走源码级编辑并记录行首光标，撤销时占位符整体恢复、光标回到该行行首。
    /// </summary>
    private bool TryDeleteListPlaceholder(Key key)
    {
        if (_editor.SelectionLength != 0)
        {
            return false;
        }

        MarkdownDocumentModel model = _model();
        string markdown = model.Markdown;
        int visibleCaret = _editor.CaretOffset;
        int visibleLineStart = _editor.Document.GetLineByOffset(visibleCaret).Offset;
        int lineStart = model.SourceOffsetFromVisible(visibleLineStart, trailingAffinity: false);
        int lineEnd = markdown.IndexOf('\n', lineStart);
        if (lineEnd < 0) lineEnd = markdown.Length;
        Match match = ListItemLine().Match(markdown[lineStart..lineEnd]);
        if (!match.Success)
        {
            return false;
        }

        int contentStart = lineStart + match.Groups[4].Index;
        int contentStartVisible = model.VisibleOffsetFromSource(contentStart);
        bool backspaceHits = key == Key.Back && visibleCaret > visibleLineStart && visibleCaret <= contentStartVisible;
        bool deleteHits = key == Key.Delete && visibleCaret >= visibleLineStart && visibleCaret < contentStartVisible;
        if (!backspaceHits && !deleteHits)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(match.Groups[4].Value))
        {
            // 空列表项上按 Backspace：整行连同相邻换行一起删除，退出列表。
            int removeStart = lineStart > 0 ? lineStart - 1 : lineStart;
            int removeEnd = lineEnd < markdown.Length && markdown[lineEnd] == '\n'
                ? lineEnd + 1
                : lineEnd;
            ApplySource(markdown, removeStart, removeEnd, string.Empty, removeStart, lineStart);
            return true;
        }
        ApplySource(markdown, lineStart, contentStart, string.Empty, lineStart, lineStart);
        return true;
    }

    /// <summary>
    /// 规则 6：续行行首的占位空格是整体。光标落在占位空格内或内容起点上时，Backspace/
    /// Delete 一次性删除全部占位空格，该行退出续行；删除走源码级编辑并记录行首光标，
    /// 撤销时占位空格整体恢复、光标回到该行行首。空续行（无内容）仍由规则 4 处理。
    /// </summary>
    private bool TryDeleteContinuationPlaceholder(Key key)
    {
        if (_editor.SelectionLength != 0)
        {
            return false;
        }

        MarkdownDocumentModel model = _model();
        string markdown = model.Markdown;
        int visibleCaret = _editor.CaretOffset;
        int visibleLineStart = _editor.Document.GetLineByOffset(visibleCaret).Offset;
        if (IsInsideCodeBlock(model, visibleLineStart))
        {
            return false;
        }
        int lineStart = model.SourceOffsetFromVisible(visibleLineStart, trailingAffinity: false);
        int lineEnd = markdown.IndexOf('\n', lineStart);
        if (lineEnd < 0) lineEnd = markdown.Length;
        string line = markdown[lineStart..lineEnd];
        // 空续行交给规则 4（连同换行一起删除），列表项行由列表占位符处理。
        if (line.Length == 0 || !char.IsWhiteSpace(line[0]) ||
            string.IsNullOrWhiteSpace(line) || ListItemLine().IsMatch(line))
        {
            return false;
        }
        // 占位宽度固定为所属列表项的内容列（对齐列）：行首多出的空格是正文，
        // Backspace 逐个删除，只有占位边界上的 Backspace 才整体删除占位。
        if (ListContentColumn(markdown, lineStart) is not { } contentColumn)
        {
            return false;
        }

        int leadingWidth = line.Length - line.TrimStart().Length;
        int placeholderEnd = lineStart + Math.Min(leadingWidth, contentColumn);
        int placeholderEndVisible = model.VisibleOffsetFromSource(placeholderEnd);
        bool backspaceHits = key == Key.Back && visibleCaret > visibleLineStart && visibleCaret <= placeholderEndVisible;
        bool deleteHits = key == Key.Delete && visibleCaret >= visibleLineStart && visibleCaret < placeholderEndVisible;
        if (!backspaceHits && !deleteHits)
        {
            return false;
        }

        ApplySource(markdown, lineStart, placeholderEnd, string.Empty, lineStart, lineStart);
        return true;
    }

    /// <summary>
    /// 向上查找续行所属的列表项行，返回其内容列宽度（缩进 + 标记 + 空格 + 任务框）；
    /// 上方不是列表项内容时返回 null。与 IsInsideListItemContent 的向上遍历一致。
    /// </summary>
    private static int? ListContentColumn(string markdown, int lineStart)
    {
        int cursor = lineStart;
        while (cursor > 0)
        {
            int previousEnd = cursor - 1;
            int previousStart = previousEnd == 0 ? 0 : markdown.LastIndexOf('\n', previousEnd - 1) + 1;
            string previous = markdown[previousStart..previousEnd];
            if (string.IsNullOrWhiteSpace(previous))
            {
                return null;
            }
            Match match = ListItemLine().Match(previous);
            if (match.Success)
            {
                return match.Groups[4].Index;
            }
            if (previous[0] is not (' ' or '\t'))
            {
                return null;
            }
            cursor = previousStart;
        }
        return null;
    }

    private static bool IsInsideCodeBlock(MarkdownDocumentModel model, int visibleOffset) =>
        model.Spans.Any(span => span.Kind == MarkdownVisualKind.CodeBlock &&
            visibleOffset >= span.Start && visibleOffset < span.End);

    /// <summary>
    /// 框选删除触及列表占位符时，占位符随选区一起整体删除（普通可见文本删除会把占位符
    /// 拆散，撤销时可见符号会被当作正文写回源码）。光标落在删除起点，撤销时回到占位符
    /// 所在行的行首。
    /// </summary>
    private bool TryDeleteSelectionOverListPlaceholder()
    {
        if (_editor.SelectionLength == 0)
        {
            return false;
        }

        MarkdownDocumentModel model = _model();
        string markdown = model.Markdown;
        int visibleStart = _editor.SelectionStart;
        int visibleEnd = visibleStart + _editor.SelectionLength;
        int? placeholderLineStart = null;
        DocumentLine line = _editor.Document.GetLineByOffset(visibleStart);
        DocumentLine lastLine = _editor.Document.GetLineByOffset(Math.Max(visibleStart, visibleEnd - 1));
        while (line is not null)
        {
            int lineStart = model.SourceOffsetFromVisible(line.Offset, trailingAffinity: false);
            int lineEnd = markdown.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = markdown.Length;
            Match match = ListItemLine().Match(markdown[lineStart..lineEnd]);
            if (match.Success)
            {
                int contentStartVisible = model.VisibleOffsetFromSource(lineStart + match.Groups[4].Index);
                if (visibleStart < contentStartVisible && visibleEnd > line.Offset)
                {
                    placeholderLineStart ??= lineStart;
                }
            }
            if (ReferenceEquals(line, lastLine))
            {
                break;
            }
            line = line.NextLine;
        }
        if (placeholderLineStart is not { } undoCaret)
        {
            return false;
        }

        (int sourceStart, int sourceEnd) = model.GetSourceDeletionRange(visibleStart, visibleEnd);
        if (sourceEnd <= sourceStart)
        {
            return false;
        }
        // 占位符的可见字符映射到整段前缀，但行首缩进逐字可见：吸收缩进，保证整体删除。
        sourceStart = Math.Min(sourceStart, undoCaret);
        ApplySource(markdown, sourceStart, sourceEnd, string.Empty, sourceStart, undoCaret);
        return true;
    }

    private bool TryDeleteEmptyCodeBlock()
    {
        if (_editor.SelectionLength != 0)
        {
            return false;
        }

        MarkdownDocumentModel model = _model();
        string markdown = model.Markdown;
        int caret = _editor.CaretOffset;
        int lineStart = _editor.Document.GetLineByOffset(caret).Offset;
        MarkdownVisualSpan? candidate = model.Spans
            .Where(span => span.Kind == MarkdownVisualKind.CodeBlock && span.CodeLabel is not null)
            .Where(span => lineStart == span.Start && caret >= span.Start && caret <= span.End)
            .Select(span => (MarkdownVisualSpan?)span)
            .FirstOrDefault();
        if (candidate is not { } codeBlock ||
            codeBlock.CodeContent is not { } content ||
            content.Contains('\n') ||
            !string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        int deleteStart = codeBlock.SourceStart;
        int deleteEnd = deleteStart + codeBlock.SourceLength;
        // Absorb the blank lines the block was sitting between so the remaining text keeps
        // one standard paragraph separator, or none when the block touches a document edge.
        int before = 0;
        while (before < 2 && deleteStart - before - 1 >= 0 && markdown[deleteStart - before - 1] == '\n')
        {
            before++;
        }
        int after = 0;
        while (after < 2 && deleteEnd + after < markdown.Length && markdown[deleteEnd + after] == '\n')
        {
            after++;
        }
        int remove = Math.Max(0, before + after - (before == 0 || after == 0 ? 0 : 2));
        int eatBefore = Math.Min(before, remove);
        ApplySource(
            markdown,
            deleteStart - eatBefore,
            deleteEnd + remove - eatBefore,
            string.Empty,
            deleteStart - eatBefore);
        return true;
    }

    private bool TryIndentList(bool outdent)
    {
        MarkdownDocumentModel model = _model();
        string markdown = model.Markdown;
        int source = model.SourceOffsetFromVisible(_editor.CaretOffset);
        int lineStart = source == 0 ? 0 : markdown.LastIndexOf('\n', source - 1) + 1;
        int lineEnd = markdown.IndexOf('\n', lineStart);
        if (lineEnd < 0) lineEnd = markdown.Length;
        if (!ListItemLine().IsMatch(markdown[lineStart..lineEnd]))
        {
            return false;
        }

        if (!outdent)
        {
            ApplySource(markdown, lineStart, lineStart, "  ", source + 2);
            return true;
        }

        int remove = markdown.AsSpan(lineStart).StartsWith("  ", StringComparison.Ordinal) ? 2 :
            markdown.AsSpan(lineStart).StartsWith("\t", StringComparison.Ordinal) ? 1 : 0;
        if (remove > 0)
        {
            ApplySource(markdown, lineStart, lineStart + remove, string.Empty, source - remove);
        }
        return true;
    }

    /// <summary>
    /// 表格下方的空行是表格的结束位置：在这里按 Backspace/Delete 不能合并换行——那会让
    /// Markdig 丢失表格块、整个表格退回成竖线源码——而是先确认、再删除整个表格（基线行为）。
    /// </summary>
    private MarkdownVisualSpan? TableBeforeEmptyTrailingLine()
    {
        if (_editor.SelectionLength != 0)
        {
            return null;
        }

        int caret = _editor.CaretOffset;
        DocumentLine line = _editor.Document.GetLineByOffset(caret);
        if (line.Length != 0 || caret != line.Offset)
        {
            return null;
        }

        return _model().Spans
            .Where(span => span.Kind == MarkdownVisualKind.Table &&
                span.SourceLength > 3 &&
                caret == span.End + 1)
            .OrderByDescending(span => span.SourceLength)
            .Select(span => (MarkdownVisualSpan?)span)
            .FirstOrDefault();
    }

    private async Task DeleteTableAfterConfirmationAsync(MarkdownVisualSpan table)
    {
        if (_tableDeleteConfirmationPending)
        {
            return;
        }

        _tableDeleteConfirmationPending = true;
        try
        {
            if (await ConfirmTableDeletionAsync())
            {
                DeleteTable(_model(), table);
            }
        }
        finally
        {
            _tableDeleteConfirmationPending = false;
        }
    }

    private Task<bool> ConfirmTableDeletionAsync()
    {
        if (TableDeleteConfirmationAsync is { } hook)
        {
            return hook();
        }

        Window? owner = Window.GetWindow(_editor);
        if (owner is null)
        {
            return Task.FromResult(false);
        }

        ConfirmDialog dialog = new("删除表格", "此空行是表格的结束位置。继续删除将删除整个表格，是否确认？");
        return Task.FromResult(dialog.ShowDialog(owner));
    }

    private void DeleteTable(MarkdownDocumentModel model, MarkdownVisualSpan table)
    {
        string markdown = model.Markdown;
        int end = table.SourceStart + table.SourceLength;
        // 连同表格后的换行一起删除，最多吸收 3 个，让前后段落保持标准的空行分隔。
        int lineBreaks = 0;
        while (lineBreaks < 3 && end + lineBreaks < markdown.Length && markdown[end + lineBreaks] == '\n')
        {
            lineBreaks++;
        }

        ApplySource(markdown, table.SourceStart, end + lineBreaks, string.Empty, table.SourceStart);
    }

    private bool TryDeleteAtomicObject(Key key)
    {
        MarkdownDocumentModel model = _model();
        int visibleStart = _editor.SelectionStart;
        int visibleEnd = visibleStart + _editor.SelectionLength;
        bool hasSelection = _editor.SelectionLength > 0;
        MarkdownVisualSpan[] objects = model.Spans
            .Where(span => span.Kind is MarkdownVisualKind.Image or MarkdownVisualKind.Rule or MarkdownVisualKind.Table)
            .Where(span => hasSelection
                ? span.Start < visibleEnd && span.End > visibleStart
                : key == Key.Delete ? span.Start == _editor.CaretOffset : span.End == _editor.CaretOffset)
            .ToArray();
        if (objects.Length == 0)
        {
            return false;
        }

        int sourceStart = objects.Min(span => span.SourceStart);
        int sourceEnd = objects.Max(span => span.SourceStart + span.SourceLength);
        if (hasSelection)
        {
            // 框选删除时必须连同选区内的普通文本一起删除，而不是只删除选区穿过的原子对象，
            // 否则选中的文字会残留。选区范围用与普通删除一致的语义映射回源码。
            (int selectionStart, int selectionEnd) = model.GetSourceDeletionRange(visibleStart, visibleEnd);
            sourceStart = Math.Min(sourceStart, selectionStart);
            sourceEnd = Math.Max(sourceEnd, selectionEnd);
        }
        ApplySource(model.Markdown, sourceStart, sourceEnd, string.Empty, sourceStart);
        return true;
    }

    private void ApplySource(string markdown, int start, int end, string replacement, int caret, int? undoCaret = null)
    {
        string result = markdown[..start] + replacement + markdown[end..];
        _applyEdit(new MarkdownEditResult(result, caret, caret, undoCaret), false);
    }

    // 与投影层 ListLinePrefix 一致：占位符只含标记后的一个空格（任务框同理），
    // Groups[4] 从占位符后的第一个字符开始，多出的空格属于正文。
    [GeneratedRegex(@"^([ \t]*)([-+*]|\d+[.)])[ \t](\[[ xX]\][ \t])?(.*)$")]
    private static partial Regex ListItemLine();

    [GeneratedRegex(@"^([ \t]*)>[ \t]?(.*)$")]
    private static partial Regex QuoteItemLine();
}

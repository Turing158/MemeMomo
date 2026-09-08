using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using MemeMomo.Markdown;
using MemeMomo.UI.Popup;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using WpfControl = System.Windows.Controls.Control;

namespace MemeMomo.Editor;

/// <summary>
/// Inline control that renders one Markdown table as editable cells. The adapter keeps one
/// instance per table and hands the same element back every time AvalonEdit rebuilds the
/// hosting visual line. Line rebuilds happen on every DesiredSize change — including a
/// wrapped cell growing taller while the user types — and re-using the element keeps the
/// focused TextBox, its caret, and an active IME composition alive across the rebuild;
/// removing and recreating the element would kick keyboard focus to the text area, which
/// finalizes the composition into the document below the table and swallows keystrokes
/// typed during the rebuild.
/// </summary>
internal sealed class MarkdownTableControl : Border
{
    private const double TableCornerRadius = 6;
    private const double TablePadding = 5;
    private const double TableMinimumColumnWidth = 72;

    private readonly AvalonEditMarkdownAdapter _adapter;
    private readonly int _tableOrdinal;
    private readonly TextView _textView;
    private readonly Grid _cellGrid = new();
    private readonly Canvas _headerLayer = new() { IsHitTestVisible = false, Focusable = false };
    private readonly Border _headerBackground = new()
    {
        CornerRadius = new CornerRadius(TableCornerRadius - 1, TableCornerRadius - 1, 0, 0),
        IsHitTestVisible = false,
        Focusable = false
    };
    private readonly Dictionary<(int Row, int Column), System.Windows.Controls.TextBox> _cells = new();
    private bool _syncing;
    private int _rowCount;
    private int _columnCount;
    private string _source = string.Empty;

    internal MarkdownTableControl(AvalonEditMarkdownAdapter adapter, int tableOrdinal)
    {
        _adapter = adapter;
        _tableOrdinal = tableOrdinal;
        _textView = adapter.Editor.TextArea.TextView;
        Margin = new Thickness(2, 5, 2, 5);
        Padding = new Thickness(TablePadding);
        CornerRadius = new CornerRadius(TableCornerRadius);
        BorderThickness = new Thickness(1);
        Background = Brushes.Transparent;
        SetResourceReference(Border.BorderBrushProperty, "MarkdownTableBorderBrush");
        MemeMomo.UI.Text.LocalizeExtension.Set(this, AutomationProperties.NameProperty, "可编辑 Markdown 表格样片");
        _headerBackground.SetResourceReference(Border.BackgroundProperty, "MarkdownTableHeaderBrush");
        _headerLayer.Children.Add(_headerBackground);
        _cellGrid.LayoutUpdated += (_, _) =>
        {
            if (_cellGrid.RowDefinitions.Count == 0)
                return;
            _headerBackground.Width = _cellGrid.ActualWidth + TablePadding * 2;
            _headerBackground.Height = _cellGrid.RowDefinitions[0].ActualHeight + TablePadding;
            Canvas.SetLeft(_headerBackground, -TablePadding);
            Canvas.SetTop(_headerBackground, -TablePadding);
        };
        Grid surface = new() { Background = Brushes.Transparent };
        surface.Children.Add(_headerLayer);
        surface.Children.Add(_cellGrid);
        Child = surface;
        // AvalonEdit measures inline objects with an unbounded width, so star-sized columns
        // collapse to their content width; pin the border to the editor's content width and
        // re-pin on Loaded because ActualWidth is still 0 while the first lines are built.
        UpdateTableWidth();
        Loaded += (_, _) =>
        {
            UpdateTableWidth();
            _textView.SizeChanged += OnTextViewSizeChanged;
        };
        Unloaded += (_, _) => _textView.SizeChanged -= OnTextViewSizeChanged;
    }

    internal bool IsKeyboardFocusWithinTable => _cells.Values.Any(value => value.IsKeyboardFocusWithin);

    /// <summary>
    /// Re-reads the table's Markdown source and refreshes the cells. Called on every visual
    /// line rebuild; while the user types this is a no-op, because the live cell content is
    /// what fed the source in the first place. External changes (undo, structural edits,
    /// document reloads) flow into the cells without replacing the control.
    /// </summary>
    internal void SyncFromSource()
    {
        if (CurrentTableSpan() is not { } span)
        {
            return;
        }
        string source = TableSource(span);
        if (source == _source)
        {
            return;
        }
        _source = source;
        List<MarkdownTableEditor.Cell> cells = MarkdownTableEditor.ParseCells(source, span.SourceStart);
        int rowCount = cells.Count == 0 ? 1 : cells.Max(cell => cell.Row + 1);
        int columnCount = cells.Count == 0 ? 1 : cells.Max(cell => cell.Column + 1);
        HashSet<(int Row, int Column)> keys = cells.Select(cell => (cell.Row, cell.Column)).ToHashSet();
        bool structureChanged = rowCount != _rowCount || columnCount != _columnCount ||
            !keys.SetEquals(_cells.Keys);
        if (structureChanged)
        {
            RebuildCells(cells, rowCount, columnCount);
        }
        else
        {
            UpdateCellTexts(cells);
        }
        RecalculateColumnWidths();
    }

    private void RebuildCells(List<MarkdownTableEditor.Cell> cells, int rowCount, int columnCount)
    {
        (int Row, int Column, int Caret)? focused = CaptureFocusedCell();
        _cellGrid.Children.Clear();
        _cellGrid.RowDefinitions.Clear();
        _cellGrid.ColumnDefinitions.Clear();
        _cells.Clear();
        _rowCount = rowCount;
        _columnCount = columnCount;
        for (int row = 0; row < rowCount; row++)
            _cellGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int column = 0; column < columnCount; column++)
        {
            // Star sizing keeps the table filling the editor width before the first recalc;
            // RecalculateColumnWidths replaces it with pixel widths that follow cell content.
            _cellGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        foreach (MarkdownTableEditor.Cell cell in cells)
        {
            AddCell(cell);
        }
        RestoreFocusedCell(focused);
    }

    private void AddCell(MarkdownTableEditor.Cell cell)
    {
        System.Windows.Controls.TextBox textBox = new()
        {
            MinHeight = 30,
            Padding = new Thickness(6, 3, 6, 3),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(
                0,
                0,
                cell.Column < _columnCount - 1 ? 1 : 0,
                cell.Row < _rowCount - 1 ? 1 : 0),
            Text = cell.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Focusable = true,
            FocusVisualStyle = null,
            Tag = cell
        };
        textBox.SetResourceReference(WpfControl.BorderBrushProperty, "MarkdownTableDividerBrush");
        MemeMomo.UI.Text.LocalizeExtension.Set(textBox, AutomationProperties.NameProperty, "表格第 {0} 行第 {1} 列", cell.Row + 1, cell.Column + 1);
        Grid.SetRow(textBox, cell.Row);
        Grid.SetColumn(textBox, cell.Column);
        textBox.TextChanged += OnCellTextChanged;
        textBox.AddHandler(Keyboard.PreviewKeyDownEvent,
            new System.Windows.Input.KeyEventHandler(OnCellPreviewKeyDown), handledEventsToo: true);
        textBox.AddHandler(Keyboard.KeyDownEvent,
            new System.Windows.Input.KeyEventHandler(OnCellKeyDown), handledEventsToo: true);
        textBox.GotKeyboardFocus += OnCellGotKeyboardFocus;
        textBox.LostKeyboardFocus += OnCellLostKeyboardFocus;
        textBox.ContextMenu = CreateCellContextMenu(cell, textBox);
        _cellGrid.Children.Add(textBox);
        _cells[(cell.Row, cell.Column)] = textBox;
    }

    /// <summary>
    /// Pushes source-driven cell text into the TextBoxes. The focused cell is never
    /// rewritten: its live text is the source of truth while the user types, and rewriting
    /// it would move the caret and finalize an active IME composition.
    /// </summary>
    private void UpdateCellTexts(List<MarkdownTableEditor.Cell> cells)
    {
        Dictionary<(int Row, int Column), MarkdownTableEditor.Cell> latest = cells
            .ToDictionary(cell => (cell.Row, cell.Column));
        _syncing = true;
        try
        {
            foreach (KeyValuePair<(int Row, int Column), System.Windows.Controls.TextBox> pair in _cells)
            {
                if (!latest.TryGetValue(pair.Key, out MarkdownTableEditor.Cell? updated))
                    continue;
                pair.Value.Tag = updated;
                if (!ReferenceEquals(Keyboard.FocusedElement, pair.Value) &&
                    pair.Value.Text != updated.Text)
                {
                    int caret = pair.Value.CaretIndex;
                    pair.Value.Text = updated.Text;
                    pair.Value.CaretIndex = Math.Min(caret, pair.Value.Text.Length);
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private (int Row, int Column, int Caret)? CaptureFocusedCell()
    {
        foreach (KeyValuePair<(int Row, int Column), System.Windows.Controls.TextBox> pair in _cells)
        {
            if (pair.Value.IsKeyboardFocusWithin)
            {
                return (pair.Key.Row, pair.Key.Column, pair.Value.CaretIndex);
            }
        }
        return null;
    }

    private void RestoreFocusedCell((int Row, int Column, int Caret)? target)
    {
        if (target is not { } focus ||
            !_cells.TryGetValue((focus.Row, focus.Column), out System.Windows.Controls.TextBox? cell))
        {
            return;
        }
        Keyboard.Focus(cell);
        cell.CaretIndex = Math.Min(focus.Caret, cell.Text.Length);
    }

    private void OnTextViewSizeChanged(object sender, SizeChangedEventArgs args) => UpdateTableWidth();

    private void UpdateTableWidth()
    {
        // TextEditor.Padding lives on the template's ScrollViewer, so the TextView's
        // ActualWidth is already the width of one text line.
        double width = _textView.ActualWidth - Margin.Left - Margin.Right;
        if (width > 0 && (!double.IsFinite(Width) || Math.Abs(Width - width) > 0.1))
        {
            Width = width;
        }
        RecalculateColumnWidths();
    }

    // Column widths follow the live cell content instead of the source snapshot the
    // control was built from: every column keeps a readable minimum and the rest of the
    // editor width is shared in proportion to each column's widest cell line, so typing
    // widens the edited column and reflows the neighbouring cells immediately. Width
    // changes only reflow inside the control — when they change a row's wrapped line
    // count the control grows, AvalonEdit rebuilds the hosting visual line, and the
    // adapter hands back this same element so nothing about the input state is disturbed.
    private void RecalculateColumnWidths()
    {
        double availableWidth = Width - Padding.Left - Padding.Right
            - BorderThickness.Left - BorderThickness.Right;
        if (_cellGrid.ColumnDefinitions.Count == 0 || !double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            return;
        }
        double[] demands = new double[_cellGrid.ColumnDefinitions.Count];
        for (int column = 0; column < demands.Length; column++)
        {
            double widest = 0;
            for (int row = 0; row < _rowCount; row++)
            {
                if (_cells.TryGetValue((row, column), out System.Windows.Controls.TextBox? cell))
                {
                    widest = Math.Max(widest, MeasureSingleLineWidth(cell));
                }
            }
            demands[column] = widest;
        }
        double[] widths = MarkdownTableEditor.ColumnWidths(demands, availableWidth, TableMinimumColumnWidth);
        for (int column = 0; column < widths.Length; column++)
        {
            _cellGrid.ColumnDefinitions[column].Width = new GridLength(widths[column], GridUnitType.Pixel);
        }
    }

    private double MeasureSingleLineWidth(System.Windows.Controls.TextBox cell)
    {
        FormattedText text = new(
            cell.Text ?? string.Empty,
            System.Globalization.CultureInfo.CurrentCulture,
            cell.FlowDirection,
            new Typeface(cell.FontFamily, cell.FontStyle, cell.FontWeight, cell.FontStretch),
            cell.FontSize,
            Brushes.Black,
            cell.IsLoaded ? VisualTreeHelper.GetDpi(cell).PixelsPerDip : 1.0);
        return text.Width + cell.Padding.Left + cell.Padding.Right
            + cell.BorderThickness.Left + cell.BorderThickness.Right;
    }

    private ContextMenu CreateCellContextMenu(MarkdownTableEditor.Cell cell, System.Windows.Controls.TextBox targetCell)
    {
        ContextMenu menu = new()
        {
            Style = Application.Current?.TryFindResource("MarkdownTableEdgeMenuPresenterTheme") as Style
        };
        ContextMenuAnimations.SetIsEnabled(menu, true);
        // 剪切/复制/粘贴放在结构操作的最顶部，与主编辑区右键菜单一致；单元格只支持
        // 文本粘贴，剪贴板只有图片时不显示粘贴项。
        EditorClipboardMenu.PrependToCellMenu(menu, targetCell);
        AddMenu("上方插入行", () => MutateTable(cell, rowInsertBefore: true, rowInsertAfter: false, rowDelete: false, columnInsertBefore: false, columnInsertAfter: false, columnDelete: false));
        AddMenu("下方插入行", () => MutateTable(cell, rowInsertBefore: false, rowInsertAfter: true, rowDelete: false, columnInsertBefore: false, columnInsertAfter: false, columnDelete: false));
        AddMenu("删除行", () => MutateTable(cell, rowInsertBefore: false, rowInsertAfter: false, rowDelete: true, columnInsertBefore: false, columnInsertAfter: false, columnDelete: false));
        menu.Items.Add(new Separator());
        AddMenu("左侧插入列", () => MutateTable(cell, rowInsertBefore: false, rowInsertAfter: false, rowDelete: false, columnInsertBefore: true, columnInsertAfter: false, columnDelete: false));
        AddMenu("右侧插入列", () => MutateTable(cell, rowInsertBefore: false, rowInsertAfter: false, rowDelete: false, columnInsertBefore: false, columnInsertAfter: true, columnDelete: false));
        AddMenu("删除列", () => MutateTable(cell, rowInsertBefore: false, rowInsertAfter: false, rowDelete: false, columnInsertBefore: false, columnInsertAfter: false, columnDelete: true));
        return menu;

        void AddMenu(string header, Action action)
        {
            MenuItem item = new()
            {
                Header = header,
                Style = Application.Current?.TryFindResource("MarkdownTableEdgeMenuItemTheme") as Style
            };
            MemeMomo.UI.Text.LocalizeExtension.Set(item, MenuItem.HeaderProperty, header);
            item.Click += (_, _) =>
            {
                action();
                ContextMenuAnimations.Close(menu);
            };
            menu.Items.Add(item);
        }
    }

    private void MutateTable(
        MarkdownTableEditor.Cell cell,
        bool rowInsertBefore,
        bool rowInsertAfter,
        bool rowDelete,
        bool columnInsertBefore,
        bool columnInsertAfter,
        bool columnDelete)
    {
        if (CurrentTableSpan() is not { } table)
        {
            return;
        }
        string source = TableSource(table);
        string next = rowInsertBefore || rowInsertAfter || rowDelete
            ? MarkdownTableEditor.MutateRow(source, cell.Row, rowInsertAfter, rowDelete)
            : MarkdownTableEditor.MutateColumn(source, cell.Column, columnInsertAfter, columnDelete);
        if (next == source)
        {
            return;
        }
        string markdown = _adapter.Model.Markdown[..table.SourceStart] + next +
            _adapter.Model.Markdown[(table.SourceStart + table.SourceLength)..];
        _adapter.ApplyEdit(new MarkdownEditResult(markdown, table.SourceStart, table.SourceStart));
    }

    private void OnCellTextChanged(object? sender, TextChangedEventArgs args)
    {
        if (_syncing || sender is not System.Windows.Controls.TextBox { Tag: MarkdownTableEditor.Cell cell } textBox ||
            textBox.Text == cell.Text)
        {
            return;
        }
        textBox.TextChanged -= OnCellTextChanged;
        try
        {
            ReplaceTableCell(cell, textBox.Text);
        }
        finally
        {
            textBox.TextChanged += OnCellTextChanged;
        }
        RecalculateColumnWidths();
    }

    private void ReplaceTableCell(MarkdownTableEditor.Cell cell, string value)
    {
        value = MarkdownTableEditor.NormalizeCellText(value);
        string oldTableSource = CurrentTableSource();
        MarkdownVisualSpan currentTable = CurrentTableSpan()
            ?? throw new InvalidOperationException("The edited Markdown table is no longer available.");
        MarkdownTableEditor.Cell currentCell = FindCell(oldTableSource, currentTable.SourceStart, cell.Row, cell.Column)
            ?? throw new InvalidOperationException("The edited Markdown table cell is no longer available.");
        int replaceStart = currentCell.SourceLength == 0 ? currentCell.ContainerStart : currentCell.SourceStart;
        int replaceLength = currentCell.SourceLength == 0 ? currentCell.ContainerLength : currentCell.SourceLength;
        string sourceValue = currentCell.SourceLength == 0 ? $" {value} " : value;
        _adapter.Model.ReplaceTableCellSourceRange(replaceStart, replaceLength, sourceValue);
        string newTableSource = CurrentTableSource();
        // Track the source this control already reflects; otherwise the next
        // SyncFromSource fast path would treat the model change as unseen and, worse,
        // an undo back to the pre-edit source would be mistaken for "unchanged".
        _source = newTableSource;
        int caretSource = currentCell.SourceLength == 0
            ? replaceStart + 1 + value.Length
            : currentCell.SourceStart + value.Length;
        int caret = _adapter.Model.VisibleOffsetFromSource(Math.Min(caretSource, _adapter.Model.Markdown.Length));
        UpdateEditedCellTag(cell, value, replaceStart, replaceLength, sourceValue.Length);
        if (!string.Equals(_adapter.Editor.Text, _adapter.Model.VisibleText, StringComparison.Ordinal))
        {
            _adapter.ScheduleProjectionRefresh(caret, caret);
        }
        _adapter.Editor.Document.UndoStack.Push(new AvalonEditMarkdownAdapter.SourceModelUndoOperation(
            () => RestoreTable(oldTableSource),
            () => RestoreTable(newTableSource)));
        _adapter.NotifyMarkdownChanged();
    }

    private void UpdateEditedCellTag(
        MarkdownTableEditor.Cell editedCell,
        string value,
        int replaceStart,
        int replaceLength,
        int replacementLength)
    {
        if (!_cells.TryGetValue((editedCell.Row, editedCell.Column), out System.Windows.Controls.TextBox? control))
        {
            return;
        }
        int delta = replacementLength - replaceLength;
        control.Tag = editedCell.SourceLength == 0
            ? new MarkdownTableEditor.Cell(
                editedCell.Row,
                editedCell.Column,
                value,
                replaceStart + 1,
                value.Length,
                replaceStart,
                replacementLength)
            : new MarkdownTableEditor.Cell(
                editedCell.Row,
                editedCell.Column,
                value,
                editedCell.SourceStart,
                value.Length,
                editedCell.ContainerStart,
                editedCell.ContainerLength + delta);
    }

    private void RestoreTable(string tableSource)
    {
        if (CurrentTableSpan() is not { } table)
        {
            return;
        }
        _adapter.Model.ReplaceSourceRange(table.SourceStart, table.SourceLength, tableSource);
        SyncFromSource();
        if (!string.Equals(_adapter.Editor.Text, _adapter.Model.VisibleText, StringComparison.Ordinal))
        {
            int restoredCaret = _adapter.Model.VisibleOffsetFromSource(table.SourceStart);
            _adapter.ScheduleProjectionRefresh(restoredCaret, restoredCaret);
        }
        else if (!IsKeyboardFocusWithinTable)
        {
            _adapter.Editor.TextArea.TextView.Redraw();
        }
        _adapter.NotifyMarkdownChanged();
    }

    private void OnCellGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs args)
    {
        _adapter.SetTableCellFocusVisual(true);
    }

    private void OnCellLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs args)
    {
        if (!IsKeyboardFocusWithinTable)
        {
            _adapter.SetTableCellFocusVisual(false);
        }
    }

    private void OnCellPreviewKeyDown(object? sender, KeyEventArgs args)
    {
        if (sender is not System.Windows.Controls.TextBox current)
        {
            return;
        }

        if (args.KeyboardDevice.Modifiers == ModifierKeys.None &&
            args.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            // Keep ordinary caret movement inside the cell. At a cell edge, the helper
            // consumes the key and moves focus to the neighbouring cell or out of the table.
            if (TryNavigateCell(current, args.Key))
            {
                args.Handled = true;
            }
            return;
        }

        if (args.Key == Key.Tab)
        {
            List<System.Windows.Controls.TextBox> ordered = _cells
                .OrderBy(pair => pair.Key.Row).ThenBy(pair => pair.Key.Column)
                .Select(pair => pair.Value).ToList();
            int index = ordered.IndexOf(current);
            int next = index + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            if (next >= 0 && next < ordered.Count)
            {
                System.Windows.Controls.TextBox target = ordered[next];
                Keyboard.Focus(target);
                target.CaretIndex = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                    ? target.Text.Length : 0;
            }
            args.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && args.Key == Key.Enter)
        {
            _adapter.NotifySaveRequested();
            args.Handled = true;
        }
        else if (args.Key == Key.Escape)
        {
            _adapter.NotifyCancelRequested();
            args.Handled = true;
        }
    }

    private static void OnCellKeyDown(object? sender, KeyEventArgs args)
    {
        // The outer AvalonEdit control owns the hidden table placeholder. Once a cell has
        // applied its normal arrow-key movement, stop the bubbling event from moving that
        // placeholder as well.
        if (args.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            args.Handled = true;
        }
    }

    private bool TryNavigateCell(System.Windows.Controls.TextBox current, Key key)
    {
        if (current.SelectionLength != 0 ||
            !_cells.ContainsValue(current))
        {
            return false;
        }

        int row = Grid.GetRow(current);
        List<System.Windows.Controls.TextBox> currentRow = CellsInRow(row);
        int cellIndex = currentRow.IndexOf(current);
        int[] rows = _cells.Keys.Select(pair => pair.Row).Distinct().OrderBy(value => value).ToArray();
        int rowIndex = Array.IndexOf(rows, row);
        if (cellIndex < 0 || rowIndex < 0)
        {
            return false;
        }

        int caret = Math.Clamp(current.CaretIndex, 0, current.Text.Length);
        bool atBoundary = key switch
        {
            Key.Left => caret == 0,
            Key.Right => caret == current.Text.Length,
            Key.Up => IsAtFirstVisualLine(current),
            Key.Down => IsAtLastVisualLine(current),
            _ => false
        };
        if (!atBoundary)
        {
            return false;
        }

        System.Windows.Controls.TextBox? target = null;
        int targetCaret = 0;
        switch (key)
        {
            case Key.Left:
                if (cellIndex > 0)
                {
                    target = currentRow[cellIndex - 1];
                    targetCaret = target.Text.Length;
                }
                else if (rowIndex > 0)
                {
                    target = CellsInRow(rows[rowIndex - 1]).LastOrDefault();
                    targetCaret = target?.Text.Length ?? 0;
                }
                else
                {
                    _adapter.TryMoveCaretOutOfTable(_tableOrdinal, moveUpward: true);
                }
                break;

            case Key.Right:
                if (cellIndex + 1 < currentRow.Count)
                {
                    target = currentRow[cellIndex + 1];
                    targetCaret = 0;
                }
                else if (rowIndex + 1 < rows.Length)
                {
                    target = CellsInRow(rows[rowIndex + 1]).FirstOrDefault();
                    targetCaret = 0;
                }
                else
                {
                    _adapter.TryMoveCaretOutOfTable(_tableOrdinal, moveUpward: false);
                }
                break;

            case Key.Up:
                if (rowIndex > 0)
                {
                    List<System.Windows.Controls.TextBox> previousRow = CellsInRow(rows[rowIndex - 1]);
                    target = previousRow[Math.Min(cellIndex, previousRow.Count - 1)];
                    targetCaret = target.Text.Length;
                }
                else
                {
                    _adapter.TryMoveCaretOutOfTable(_tableOrdinal, moveUpward: true);
                }
                break;

            case Key.Down:
                if (rowIndex + 1 < rows.Length)
                {
                    List<System.Windows.Controls.TextBox> nextRow = CellsInRow(rows[rowIndex + 1]);
                    target = nextRow[Math.Min(cellIndex, nextRow.Count - 1)];
                    targetCaret = 0;
                }
                else
                {
                    _adapter.TryMoveCaretOutOfTable(_tableOrdinal, moveUpward: false);
                }
                break;
        }

        if (target is null)
        {
            return true;
        }

        FocusNavigatedCell(target, targetCaret);
        return true;
    }

    private List<System.Windows.Controls.TextBox> CellsInRow(int row) => _cells
            .Where(pair => pair.Key.Row == row)
            .OrderBy(pair => pair.Key.Column)
            .Select(pair => pair.Value)
            .ToList();

    private static bool IsAtFirstVisualLine(System.Windows.Controls.TextBox cell) =>
        GetCaretLine(cell) <= 0;

    private static bool IsAtLastVisualLine(System.Windows.Controls.TextBox cell)
    {
        int lineCount = Math.Max(1, cell.LineCount);
        return GetCaretLine(cell) >= lineCount - 1;
    }

    private static int GetCaretLine(System.Windows.Controls.TextBox cell)
    {
        try
        {
            return cell.GetLineIndexFromCharacterIndex(
                Math.Clamp(cell.CaretIndex, 0, cell.Text.Length));
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    private static void FocusNavigatedCell(System.Windows.Controls.TextBox target, int requestedCaret)
    {
        Keyboard.Focus(target);
        target.CaretIndex = Math.Clamp(requestedCaret, 0, target.Text.Length);
        target.SelectionStart = target.CaretIndex;
        target.SelectionLength = 0;
        target.BringIntoView();
    }

    private MarkdownVisualSpan? CurrentTableSpan() => _adapter.Model.Spans
        .Where(value => value.Kind == MarkdownVisualKind.Table)
        .OrderBy(value => value.SourceStart)
        .Skip(_tableOrdinal)
        .Select(value => (MarkdownVisualSpan?)value)
        .FirstOrDefault();

    private string CurrentTableSource()
    {
        MarkdownVisualSpan table = CurrentTableSpan()
            ?? throw new InvalidOperationException("The edited Markdown table is no longer available.");
        return TableSource(table);
    }

    private string TableSource(MarkdownVisualSpan table)
    {
        int start = Math.Clamp(table.SourceStart, 0, _adapter.Model.Markdown.Length);
        int length = Math.Clamp(table.SourceLength, 0, _adapter.Model.Markdown.Length - start);
        return _adapter.Model.Markdown.Substring(start, length);
    }

    private static MarkdownTableEditor.Cell? FindCell(string source, int sourceStart, int row, int column) =>
        MarkdownTableEditor.ParseCells(source, sourceStart)
            .FirstOrDefault(cell => cell.Row == row && cell.Column == column);
}

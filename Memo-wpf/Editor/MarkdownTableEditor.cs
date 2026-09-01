using System.Text;

namespace Memo.Editor;

/// <summary>
/// Markdown table parsing and structural mutations shared by inline table controls and tests.
/// Source rows keep their original line endings; cell newlines are normalized to spaces on write.
/// </summary>
internal static class MarkdownTableEditor
{
    internal sealed record Cell(
        int Row,
        int Column,
        string Text,
        int SourceStart,
        int SourceLength,
        int ContainerStart,
        int ContainerLength);

    internal static List<Cell> ParseCells(string source, int sourceStart)
    {
        List<Cell> cells = [];
        string[] lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int lineOffset = 0;
        for (int row = 0; row < lines.Length; row++)
        {
            string line = lines[row];
            if (row == 1 || string.IsNullOrWhiteSpace(line))
            {
                lineOffset += line.Length + 1;
                continue;
            }
            int cursor = 0;
            int column = 0;
            while (cursor < line.Length)
            {
                int pipe = line.IndexOf('|', cursor);
                if (pipe < 0) break;
                int contentStart = pipe + 1;
                int nextPipe = line.IndexOf('|', contentStart);
                if (nextPipe < 0) break;
                int rawStart = contentStart;
                while (rawStart < nextPipe && char.IsWhiteSpace(line[rawStart])) rawStart++;
                int rawEnd = nextPipe;
                while (rawEnd > rawStart && char.IsWhiteSpace(line[rawEnd - 1])) rawEnd--;
                cells.Add(new Cell(
                    row,
                    column,
                    NormalizeCellText(line[rawStart..rawEnd]),
                    sourceStart + lineOffset + rawStart,
                    rawEnd - rawStart,
                    sourceStart + lineOffset + contentStart,
                    nextPipe - contentStart));
                column++;
                cursor = nextPipe;
            }
            lineOffset += line.Length + 1;
        }
        return cells;
    }

    internal static string NormalizeCellText(string? value) =>
        (value ?? string.Empty).Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();

    internal static string EmptyRow(int columns) =>
        "| " + string.Join(" | ", Enumerable.Repeat(string.Empty, Math.Max(1, columns))) + " |";

    internal static string DividerRow(int columns) =>
        "| " + string.Join(" | ", Enumerable.Repeat("---", Math.Max(1, columns))) + " |";

    internal static string MutateRow(string source, int row, bool insertAfter, bool delete)
    {
        string[] lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int columns = ParseCells(source, 0).Select(cell => cell.Column).DefaultIfEmpty(0).Max() + 1;
        row = Math.Clamp(row, 0, Math.Max(0, lines.Length - 1));

        // The divider line is not exposed as a cell, so cell rows are 0 (header),
        // 2+ (body). Keep deletion separate from insertion: when a protected/last
        // row cannot be deleted it must be a no-op, never fall through to inserting
        // an empty row.
        int[] bodyRows = ParseCells(source, 0)
            .Where(cell => cell.Row >= 2)
            .Select(cell => cell.Row)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        if (delete)
        {
            if (row == 0 && bodyRows.Length > 0)
            {
                // A Markdown table needs its delimiter immediately below the header.
                // Deleting the header therefore promotes the first body row and removes
                // that row's old position, leaving a valid table with one fewer row.
                int firstBodyRow = bodyRows[0];
                lines[0] = lines[firstBodyRow];
                lines = lines.Where((_, index) => index != firstBodyRow).ToArray();
            }
            else if (row >= 2 && bodyRows.Length > 1 && bodyRows.Contains(row))
            {
                lines = lines.Where((_, index) => index != row).ToArray();
            }
            else
            {
                return string.Join("\n", lines).TrimEnd('\n');
            }
        }
        else
        {
            List<string> output = [.. lines];
            if (row == 0)
            {
                if (insertAfter)
                {
                    // The row below the header is after the delimiter.
                    output.Insert(Math.Min(output.Count, 2), EmptyRow(columns));
                }
                else
                {
                    // Insert a new header and keep the delimiter in row 1. The old
                    // header becomes the first body row.
                    output.Insert(0, EmptyRow(columns));
                    if (output.Count > 2)
                    {
                        (output[1], output[2]) = (output[2], output[1]);
                    }
                }
            }
            else
            {
                int targetRow = row == 1 ? 2 : row;
                int insertionIndex = insertAfter
                    ? Math.Min(output.Count, targetRow + 1)
                    : Math.Clamp(targetRow, 2, output.Count);
                output.Insert(insertionIndex, EmptyRow(columns));
            }
            lines = output.ToArray();
        }
        return string.Join("\n", lines).TrimEnd('\n');
    }

    internal static string MutateColumn(string source, int column, bool insertAfter, bool delete)
    {
        string[] lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int columns = ParseCells(source, 0).Select(cell => cell.Column).DefaultIfEmpty(0).Max() + 1;
        column = Math.Clamp(column, 0, Math.Max(0, columns - 1));
        List<string> output = [];
        for (int row = 0; row < lines.Length; row++)
        {
            string line = lines[row];
            if (string.IsNullOrWhiteSpace(line))
            {
                output.Add(line);
                continue;
            }
            List<string> cells = SplitRow(line);
            if (row == 1)
            {
                if (delete && cells.Count > 1) cells.RemoveAt(column);
                else if (insertAfter) cells.Insert(Math.Min(cells.Count, column + 1), "---");
                else cells.Insert(Math.Clamp(column, 0, cells.Count), "---");
            }
            else
            {
                if (delete && cells.Count > 1) cells.RemoveAt(column);
                else if (insertAfter) cells.Insert(Math.Min(cells.Count, column + 1), string.Empty);
                else cells.Insert(Math.Clamp(column, 0, cells.Count), string.Empty);
            }
            output.Add("| " + string.Join(" | ", cells.Select(NormalizeCellText)) + " |");
        }
        return string.Join("\n", output).TrimEnd('\n');
    }

    /// <summary>
    /// Allocates pixel column widths for a table spanning the full editor width: every
    /// column keeps a readable minimum and the remaining width is shared in proportion to
    /// each column's measured content demand, so columns widen and narrow as cell content
    /// changes. The last column absorbs rounding so the widths always fill the available
    /// width exactly; below the readable minimum the columns split equally instead.
    /// </summary>
    internal static double[] ColumnWidths(double[] demands, double availableWidth, double minimumColumnWidth)
    {
        int columns = demands.Length;
        double[] widths = new double[columns];
        if (columns == 0 || !double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            return widths;
        }
        if (availableWidth < minimumColumnWidth * columns)
        {
            double allocated = 0;
            for (int column = 0; column < columns; column++)
            {
                double width = column == columns - 1 ? availableWidth - allocated : availableWidth / columns;
                widths[column] = Math.Max(0, width);
                allocated += width;
            }
            return widths;
        }

        double remaining = availableWidth - minimumColumnWidth * columns;
        double totalDemand = demands.Sum(demand => Math.Max(1, demand));
        double allocatedWidth = 0;
        for (int column = 0; column < columns; column++)
        {
            double width = column == columns - 1
                ? availableWidth - allocatedWidth
                : minimumColumnWidth + remaining * Math.Max(1, demands[column]) / totalDemand;
            widths[column] = Math.Max(0, width);
            allocatedWidth += width;
        }
        return widths;
    }

    private static List<string> SplitRow(string line)
    {
        int start = line.StartsWith('|') ? 1 : 0;
        int end = line.EndsWith('|') ? line.Length - 1 : line.Length;
        return line[start..end].Split('|').Select(NormalizeCellText).ToList();
    }
}

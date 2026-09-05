using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using MemeMomo.Markdown;
using MemeMomo.UI;

namespace MemeMomo.Editor;

internal class MarkdownObjectElementGenerator(
    Func<IReadOnlyList<MarkdownVisualSpan>> spans,
    Func<MarkdownVisualSpan, VisualLineElement>? elementFactory = null) : VisualLineElementGenerator
{
    private readonly Func<MarkdownVisualSpan, VisualLineElement> _elementFactory =
        elementFactory ?? (span => new InlineObjectElement(span.Length, CreateControl(span)));

    internal long CreatedElementCount { get; private set; }

    public override int GetFirstInterestedOffset(int startOffset) => spans()
        .Where(IsInlineObject)
        .Select(span => span.Start)
        .Where(offset => offset >= startOffset)
        .DefaultIfEmpty(-1)
        .Min();

    public override VisualLineElement? ConstructElement(int offset)
    {
        MarkdownVisualSpan? match = spans()
            .Where(IsInlineObject)
            .Select(span => (MarkdownVisualSpan?)span)
            .FirstOrDefault(span => span!.Value.Start == offset);
        if (match is not { } span)
        {
            return null;
        }

        CreatedElementCount++;
        return _elementFactory(span);
    }

    private static bool IsInlineObject(MarkdownVisualSpan span) =>
        span.Length > 0 && span.Kind is MarkdownVisualKind.Image or MarkdownVisualKind.Table or MarkdownVisualKind.Task;

    private static UIElement CreateControl(MarkdownVisualSpan span) => span.Kind switch
    {
        MarkdownVisualKind.Table => CreateTableControl(),
        MarkdownVisualKind.Task => CreateTaskControl(),
        _ => CreateImageControl(span.AltText),
    };

    private static System.Windows.Controls.CheckBox CreateTaskControl()
    {
        System.Windows.Controls.CheckBox checkBox = TaskCheckBoxFactory.Create();
        AutomationProperties.SetName(checkBox, "任务复选框");
        return checkBox;
    }

    private static Border CreateImageControl(string? altText)
    {
        Border border = new()
        {
            Width = 132,
            Height = 72,
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(205, 205, 205)),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 245, 245)),
            Child = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(altText) ? "图片" : altText,
                Margin = new Thickness(8),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            }
        };
        AutomationProperties.SetName(border, $"图片：{altText ?? ""}");
        return border;
    }

    private static Border CreateTableControl()
    {
        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        for (int column = 0; column < 2; column++)
        {
            System.Windows.Controls.TextBox cell = new()
            {
                MinHeight = 30,
                Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(column == 0 ? 0 : -1, 0, 0, 0),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 210, 210)),
                BorderThickness = new Thickness(1),
                Text = column == 0 ? "单元格 A" : "单元格 B",
                AcceptsReturn = true,
                Focusable = true
            };
            AutomationProperties.SetName(cell, $"表格第 1 行第 {column + 1} 列");
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }

        Border border = new()
        {
            Margin = new Thickness(2, 3, 2, 3),
            Child = grid
        };
        AutomationProperties.SetName(border, "可编辑 Markdown 表格样片");
        return border;
    }
}

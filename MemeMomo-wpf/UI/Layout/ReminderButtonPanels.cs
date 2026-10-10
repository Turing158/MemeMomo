using System.Windows;
using System.Windows.Controls;
using Size = System.Windows.Size;

namespace MemeMomo.UI.Layout;

/// <summary>Uses the measured labels, including localization, to choose four or two columns.</summary>
public sealed class ReminderQuickTimesPanel : System.Windows.Controls.Panel
{
    internal int Columns { get; private set; } = 4;
    private double _rowHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        double naturalWidth = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            naturalWidth = Math.Max(naturalWidth, child.DesiredSize.Width);
        }
        Columns = naturalWidth * 4 <= availableSize.Width ? 4 : 2;
        double width = double.IsPositiveInfinity(availableSize.Width) ? naturalWidth * Columns : availableSize.Width;
        _rowHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(width / Columns, double.PositiveInfinity));
            _rowHeight = Math.Max(_rowHeight, child.DesiredSize.Height);
        }
        return new Size(width, Math.Ceiling((double)InternalChildren.Count / Columns) * _rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double cellWidth = finalSize.Width / Columns;
        for (int i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(new Rect(i % Columns * cellWidth, i / Columns * _rowHeight, cellWidth, _rowHeight));
        return finalSize;
    }
}

/// <summary>Keeps the cancel/save pair together and gives the optional clear action its own row when needed.</summary>
public sealed class ReminderActionsPanel : System.Windows.Controls.Panel
{
    private const double Gap = 8;
    private double _cancelWidth;
    private double _saveWidth;
    private double _clearHeight;
    private double _pairHeight;
    internal bool IsStacked { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count != 3) return new Size();
        UIElement clear = InternalChildren[0], cancel = InternalChildren[1], save = InternalChildren[2];
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bool hasClear = clear.Visibility != Visibility.Collapsed;
        double naturalWidth = cancel.DesiredSize.Width + save.DesiredSize.Width + Gap;
        if (hasClear) naturalWidth += clear.DesiredSize.Width + Gap;
        double width = double.IsPositiveInfinity(availableSize.Width) ? naturalWidth : availableSize.Width;
        IsStacked = hasClear && naturalWidth > width;
        double pairWidth = Math.Max(0, width - Gap - (hasClear && !IsStacked ? clear.DesiredSize.Width + Gap : 0));
        _cancelWidth = Math.Min(cancel.DesiredSize.Width, pairWidth / 2);
        _saveWidth = Math.Min(save.DesiredSize.Width, Math.Max(0, pairWidth - _cancelWidth));
        cancel.Measure(new Size(_cancelWidth, double.PositiveInfinity));
        save.Measure(new Size(_saveWidth, double.PositiveInfinity));
        clear.Measure(new Size(IsStacked ? width : Math.Max(0, width - _cancelWidth - _saveWidth - Gap * 2), double.PositiveInfinity));
        _clearHeight = hasClear ? clear.DesiredSize.Height : 0;
        _pairHeight = Math.Max(cancel.DesiredSize.Height, save.DesiredSize.Height);
        return new Size(width, IsStacked ? _clearHeight + Gap + _pairHeight : Math.Max(_clearHeight, _pairHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 3) return finalSize;
        double y = IsStacked ? _clearHeight + Gap : 0;
        InternalChildren[0].Arrange(new Rect(0, 0,
            IsStacked ? finalSize.Width : Math.Max(0, finalSize.Width - _cancelWidth - _saveWidth - Gap * 2), _clearHeight));
        InternalChildren[1].Arrange(new Rect(Math.Max(0, finalSize.Width - _saveWidth - Gap - _cancelWidth), y, _cancelWidth, _pairHeight));
        InternalChildren[2].Arrange(new Rect(Math.Max(0, finalSize.Width - _saveWidth), y, _saveWidth, _pairHeight));
        return finalSize;
    }
}

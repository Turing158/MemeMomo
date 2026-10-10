using System.Windows;
using System.Windows.Input;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace MemeMomo.UI.Layout;

/// <summary>
/// Hosts count, kind, status and recycle tools in that order. Measures their
/// localized content before assigning space, so long labels move filters to a second row.
/// </summary>
public sealed class MemoListToolsPanel : Panel
{
    private const double RowGap = 4;
    private readonly double[] _widths = new double[4];
    private bool _stacked;
    private double _firstHeight;
    private double _secondHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count != _widths.Length) return base.MeasureOverride(availableSize);
        double naturalWidth = 0;
        for (int i = 0; i < _widths.Length; i++)
        {
            InternalChildren[i].Measure(new Size(double.PositiveInfinity, availableSize.Height));
            naturalWidth += _widths[i] = InternalChildren[i].DesiredSize.Width;
        }
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : naturalWidth;
        _stacked = naturalWidth > width + 0.5;
        double filterWidth = _stacked ? width : Math.Max(0, width - _widths[0] - _widths[3]);
        // Both filters share the available width and resize together.
        _widths[1] = filterWidth / 2;
        _widths[2] = Math.Max(0, filterWidth - _widths[1]);
        _widths[0] = Math.Max(0, (_stacked ? width : width - filterWidth) - _widths[3]);
        _firstHeight = _secondHeight = 0;
        for (int i = 0; i < _widths.Length; i++)
        {
            InternalChildren[i].Measure(new Size(_widths[i], availableSize.Height));
            double height = InternalChildren[i].DesiredSize.Height;
            if (_stacked && i is 1 or 2) _secondHeight = Math.Max(_secondHeight, height);
            else _firstHeight = Math.Max(_firstHeight, height);
            KeyboardNavigation.SetTabIndex(InternalChildren[i], _stacked ? i switch { 3 => 1, 1 => 2, 2 => 3, _ => 0 } : i);
        }
        return new Size(width, _firstHeight + (_stacked ? RowGap + _secondHeight : 0));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != _widths.Length) return base.ArrangeOverride(finalSize);
        double x = 0;
        foreach (int i in _stacked ? new[] { 0, 3 } : new[] { 0, 1, 2, 3 })
        {
            InternalChildren[i].Arrange(new Rect(x, 0, _widths[i], _firstHeight));
            x += _widths[i];
        }
        if (_stacked)
        {
            InternalChildren[1].Arrange(new Rect(0, _firstHeight + RowGap, _widths[1], _secondHeight));
            InternalChildren[2].Arrange(new Rect(_widths[1], _firstHeight + RowGap, _widths[2], _secondHeight));
        }
        return finalSize;
    }
}

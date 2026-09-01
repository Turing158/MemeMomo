using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Memo.UI;
using CheckBox = System.Windows.Controls.CheckBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Memo.Components;

/// <summary>A standard CheckBox with Memo's visual states and native toggle semantics.</summary>
public partial class AnimatedCheckBox : CheckBox
{
    private const double CheckedPressedScale = 0.7;
    private const double DefaultPressedScale = 0.94;

    public AnimatedCheckBox()
    {
        InitializeComponent();
        _ = new CheckBoxVisualAnimator(
            this,
            "IndicatorBaseLayer",
            "PART_CheckMark",
            "IndicatorPressPreview",
            "PART_IndeterminateMark");
        UpdatePressedScale();
    }

    protected override void OnChecked(RoutedEventArgs e)
    {
        UpdatePressedScale();
        base.OnChecked(e);
    }

    protected override void OnUnchecked(RoutedEventArgs e)
    {
        UpdatePressedScale();
        base.OnUnchecked(e);
    }

    protected override void OnIndeterminate(RoutedEventArgs e)
    {
        UpdatePressedScale();
        base.OnIndeterminate(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsEnabled && e.Key == Key.Space)
        {
            IsChecked = IsChecked != true;
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void UpdatePressedScale() =>
        InteractionAnimations.SetPressedScale(
            this,
            IsChecked == true ? CheckedPressedScale : DefaultPressedScale);
}

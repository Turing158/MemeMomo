using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Memo.UI.Windows;
using WpfBrush = System.Windows.Media.Brush;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfSize = System.Windows.Size;

namespace Memo.UI.Text;

public sealed class LetterSpacedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(LetterSpacedText),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LetterSpacingProperty = DependencyProperty.Register(
        nameof(LetterSpacing),
        typeof(double),
        typeof(LetterSpacedText),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily),
        typeof(WpfFontFamily),
        typeof(LetterSpacedText),
        new FrameworkPropertyMetadata(new WpfFontFamily("Microsoft YaHei UI"), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize),
        typeof(double),
        typeof(LetterSpacedText),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground),
        typeof(WpfBrush),
        typeof(LetterSpacedText),
        new FrameworkPropertyMetadata(System.Windows.Media.Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
        nameof(FontWeight),
        typeof(FontWeight),
        typeof(LetterSpacedText),
        new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontStyleProperty = DependencyProperty.Register(
        nameof(FontStyle),
        typeof(System.Windows.FontStyle),
        typeof(LetterSpacedText),
        new FrameworkPropertyMetadata(FontStyles.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double LetterSpacing
    {
        get => (double)GetValue(LetterSpacingProperty);
        set => SetValue(LetterSpacingProperty, value);
    }

    public WpfFontFamily FontFamily
    {
        get => (WpfFontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public WpfBrush Foreground
    {
        get => (WpfBrush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public System.Windows.FontStyle FontStyle
    {
        get => (System.Windows.FontStyle)GetValue(FontStyleProperty);
        set => SetValue(FontStyleProperty, value);
    }

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        IReadOnlyList<FormattedText> runs = CreateRuns();
        double width = runs.Sum(run => run.WidthIncludingTrailingWhitespace) +
            Math.Max(0, runs.Count - 1) * LetterSpacing;
        double height = runs.Count == 0 ? 0 : runs.Max(run => run.Height);
        return new WpfSize(Math.Min(width, availableSize.Width), Math.Min(height, availableSize.Height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double x = 0;
        foreach (FormattedText run in CreateRuns())
        {
            drawingContext.DrawText(run, new System.Windows.Point(x, 0));
            x += run.WidthIncludingTrailingWhitespace + LetterSpacing;
        }
    }

    private IReadOnlyList<FormattedText> CreateRuns()
    {
        List<FormattedText> runs = [];
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(Text ?? string.Empty);
        while (enumerator.MoveNext())
        {
            runs.Add(new FormattedText(
                enumerator.GetTextElement(),
                CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyle, FontWeight, FontStretches.Normal),
                FontSize,
                Foreground,
                DpiCoordinateModel.FromVisual(this).ScaleX));
        }

        return runs;
    }
}

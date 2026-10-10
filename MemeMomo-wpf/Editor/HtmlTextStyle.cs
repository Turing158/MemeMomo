using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using MemeMomo.Markdown;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;

namespace MemeMomo.Editor;

internal static class HtmlTextStyle
{
    internal static void Apply(VisualLineElement element, HtmlPresentation? style)
    {
        if (style is null) return;
        var properties = element.TextRunProperties;
        Typeface current = properties.Typeface;
        FontFamily family = string.IsNullOrWhiteSpace(style.FontFamily) ? current.FontFamily : new FontFamily(style.FontFamily);
        properties.SetTypeface(new Typeface(family,
            style.Italic is { } italic ? italic ? FontStyles.Italic : FontStyles.Normal : current.Style,
            style.Bold is { } bold ? bold ? FontWeights.Bold : FontWeights.Normal : current.Weight, current.Stretch));
        if (style.FontSize is { } size) properties.SetFontRenderingEmSize(Math.Clamp(size, 4, 200));
        else if (style.FontScale is { } scale) properties.SetFontRenderingEmSize(Math.Clamp(properties.FontRenderingEmSize * scale, 4, 200));
        if (BrushFromCss(style.Foreground) is { } foreground) properties.SetForegroundBrush(foreground);
        if (BrushFromCss(style.Background) is { } background) properties.SetBackgroundBrush(background);
        if (style.Decoration is { } decoration)
        {
            TextDecorationCollection decorations = new();
            if (decoration.Contains("underline", StringComparison.OrdinalIgnoreCase)) decorations.Add(TextDecorations.Underline);
            if (decoration.Contains("line-through", StringComparison.OrdinalIgnoreCase)) decorations.Add(TextDecorations.Strikethrough);
            if (decoration.Contains("overline", StringComparison.OrdinalIgnoreCase)) decorations.Add(TextDecorations.OverLine);
            properties.SetTextDecorations(decorations);
        }
        if (style.Baseline is "sub" or "sup" or "super")
            properties.SetBaselineAlignment(style.Baseline == "sub" ? BaselineAlignment.Subscript : BaselineAlignment.Superscript);
    }

    internal static Brush? BrushFromCss(string? css)
    {
        if (string.IsNullOrWhiteSpace(css)) return null;
        css = css.Trim();
        try
        {
            Color color;
            Match rgb = Regex.Match(css, @"^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*([\d.]+))?\s*\)$", RegexOptions.IgnoreCase);
            if (rgb.Success)
            {
                byte Channel(int index) => (byte)Math.Clamp(int.Parse(rgb.Groups[index].Value, CultureInfo.InvariantCulture), 0, 255);
                byte alpha = rgb.Groups[4].Success ? (byte)Math.Clamp(double.Parse(rgb.Groups[4].Value, CultureInfo.InvariantCulture) * 255, 0, 255) : (byte)255;
                color = Color.FromArgb(alpha, Channel(1), Channel(2), Channel(3));
            }
            else
            {
                if (css.StartsWith('#') && css.Length == 9) css = "#" + css[7..9] + css[1..7];
                else if (css.StartsWith('#') && css.Length == 5) css = "#" + css[4] + css[1..4];
                color = (Color)ColorConverter.ConvertFromString(css);
            }
            SolidColorBrush brush = new(color);
            brush.Freeze();
            return brush;
        }
        catch (Exception error) when (error is FormatException or ArgumentException or NotSupportedException or OverflowException)
        {
            return null;
        }
    }
}

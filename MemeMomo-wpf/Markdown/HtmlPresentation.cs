using System.Globalization;

namespace MemeMomo.Markdown;

internal sealed record HtmlPresentation(
    string? Alignment = null,
    string? Foreground = null,
    string? Background = null,
    string? FontFamily = null,
    double? FontSize = null,
    double? FontScale = null,
    bool? Bold = null,
    bool? Italic = null,
    string? Decoration = null,
    string? Baseline = null,
    double? Width = null,
    double? Height = null,
    double? WidthPercent = null,
    string? Title = null)
{
    internal static HtmlPresentation Parse(string tag, IReadOnlyDictionary<string, string> attributes)
    {
        var styles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (attributes.TryGetValue("style", out string? css))
        {
            foreach (string declaration in css.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = declaration.IndexOf(':');
                if (colon > 0)
                    styles[declaration[..colon].Trim()] = declaration[(colon + 1)..].Trim();
            }
        }
        string? Value(string cssName, string? attribute = null) => styles.GetValueOrDefault(cssName) ??
            (attribute is not null ? attributes.GetValueOrDefault(attribute) : null);
        string? align = Value("text-align", "align")?.ToLowerInvariant();
        if (tag == "center") align ??= "center";
        if (align is not ("left" or "center" or "right" or "justify")) align = null;
        (double? width, double? widthPercent) = Length(Value("width", "width"));
        (double? height, _) = Length(Value("height", "height"));
        if (tag == "hr" && height is null) (height, _) = Length(attributes.GetValueOrDefault("size"));
        (double? size, double? sizePercent) = Length(Value("font-size"));
        double? scale = sizePercent / 100;
        string? fontSize = Value("font-size");
        if (fontSize?.EndsWith("em", StringComparison.OrdinalIgnoreCase) == true &&
            double.TryParse(fontSize[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double em) && em > 0)
            scale = Math.Clamp(em, 0.25, 8);
        if (size is null && scale is null && tag == "font" && attributes.TryGetValue("size", out string? legacySize) &&
            int.TryParse(legacySize, out int level))
            size = new double[] { 10, 12, 14, 18, 24, 32, 48 }[Math.Clamp(level, 1, 7) - 1];
        if (tag is "small" or "sub" or "sup") scale ??= 0.8;
        if (tag == "big") scale ??= 1.2;
        string? weight = Value("font-weight")?.ToLowerInvariant();
        bool? bold = weight switch
        {
            "bold" or "bolder" => true,
            "normal" or "lighter" => false,
            _ => int.TryParse(weight, out int numericWeight) ? numericWeight >= 600 : null
        };
        bool? italic = Value("font-style")?.ToLowerInvariant() switch
        {
            "italic" or "oblique" => true,
            "normal" => false,
            _ => null
        };
        string? baseline = Value("vertical-align")?.ToLowerInvariant();
        if (tag is "sub" or "sup") baseline ??= tag;
        return new HtmlPresentation(align, Value("color", "color"), Value("background-color", "bgcolor") ?? Value("background"),
            Value("font-family", "face")?.Replace("\"", "").Replace("'", ""), size, scale, bold, italic,
            Value("text-decoration"), baseline, width, height, widthPercent, attributes.GetValueOrDefault("title"));
    }

    private static (double? Pixels, double? Percent) Length(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, null);
        value = value.Trim();
        bool percent = value.EndsWith('%');
        bool points = value.EndsWith("pt", StringComparison.OrdinalIgnoreCase);
        string number = percent ? value[..^1] : points || value.EndsWith("px", StringComparison.OrdinalIgnoreCase) ? value[..^2] : value;
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
            !double.IsFinite(parsed) || parsed <= 0) return (null, null);
        return percent ? (null, Math.Clamp(parsed, 1, 1000)) : (Math.Clamp(parsed * (points ? 4d / 3 : 1), 1, 10000), null);
    }
}

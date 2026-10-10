using System.Windows;
using MemeMomo.Models;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace MemeMomo.UI;

internal sealed record MemoColorOption(MemoColorPreset Preset, string Name, string Background);

/// <summary>Palettes scoped to a memo surface; application resources are never modified.</summary>
internal static class MemoWindowPalette
{
    internal static IReadOnlyList<MemoColorOption> Options { get; } =
    [
        new(MemoColorPreset.FollowTheme, "跟随主题", ""),
        new(MemoColorPreset.Charcoal, "炭黑（当前）", "#202225"),
        new(MemoColorPreset.Paper, "米白", "#F5F1E8"),
        new(MemoColorPreset.MistBlue, "雾蓝", "#E9F0F6"),
        new(MemoColorPreset.Sage, "鼠尾草绿", "#EAF0E8"),
        new(MemoColorPreset.Rose, "浅玫瑰", "#F5EAEC"),
        new(MemoColorPreset.Lavender, "淡紫", "#EEECF6"),
        new(MemoColorPreset.Sand, "暖沙", "#F6EFDD")
    ];

    internal static MemoColorPreset Normalize(MemoColorPreset preset) =>
        Enum.IsDefined(preset) ? preset : MemoColorPreset.FollowTheme;

    internal static WpfColor Background(MemoColorPreset preset)
    {
        MemoColorOption option = Options[(int)Normalize(preset)];
        return Parse(option.Preset == MemoColorPreset.FollowTheme
            ? ThemePreferences.GetPalette(ThemePreferences.IsDark)["BgPrimary"]
            : option.Background);
    }

    internal static void Apply(ResourceDictionary resources, MemoColorPreset preset)
    {
        resources.Clear();
        if (Normalize(preset) == MemoColorPreset.FollowTheme)
        {
            return;
        }

        WpfColor background = Background(preset);
        bool dark = Luminance(background) < 0.3;
        Dictionary<string, WpfColor> palette = ThemePreferences.GetPalette(dark)
            .ToDictionary(pair => pair.Key, pair => Parse(pair.Value));
        WpfColor text = dark ? Parse("#E1E4E8") : Parse("#29323D");
        WpfColor accent = EnsureContrast(
            Parse(ThemePreferences.GetPalette(ThemePreferences.IsDark)["AccentPrimary"]), background, text);
        WpfColor hoverAccent = Mix(accent, dark ? Parse("#FFFFFF") : text, 0.12);

        palette["BgPrimary"] = background;
        palette["BgSecondary"] = Mix(background, text, 0.025);
        palette["BgTertiary"] = Mix(background, text, 0.07);
        palette["BgHover"] = Mix(background, text, 0.10);
        palette["SurfacePrimary"] = Mix(background, dark ? text : Parse("#FFFFFF"), dark ? 0.035 : 0.3);
        palette["SurfaceHover"] = Mix(background, text, 0.06);
        palette["SurfaceActive"] = Mix(background, text, 0.09);
        palette["BorderSubtle"] = Mix(background, text, 0.12);
        palette["BorderDefault"] = Mix(background, text, 0.20);
        palette["BorderHover"] = Mix(background, text, 0.30);
        palette["BorderEmphasis"] = Mix(background, text, 0.43);
        palette["BorderFocus"] = accent;
        palette["AccentPrimary"] = accent;
        palette["AccentHover"] = hoverAccent;
        palette["AccentPressed"] = Mix(accent, dark ? background : text, 0.12);
        palette["AccentMuted"] = Mix(background, accent, 0.45);
        palette["AccentSubtle"] = Mix(background, accent, 0.10);
        palette["AccentSubtlePressed"] = Mix(background, accent, 0.22);
        WpfColor danger = EnsureContrast(palette["DangerPrimary"], background, text);
        WpfColor success = EnsureContrast(palette["SuccessPrimary"], background, text);
        palette["DangerPrimary"] = danger;
        palette["DangerHover"] = Mix(danger, text, 0.12);
        palette["DangerSubtle"] = Mix(background, danger, 0.10);
        palette["SuccessPrimary"] = success;
        palette["SuccessHover"] = Mix(success, text, 0.12);
        palette["SuccessPressed"] = Mix(success, dark ? background : text, 0.12);
        palette["SuccessSubtle"] = Mix(background, success, 0.10);
        palette["TextPrimary"] = text;
        palette["TextSecondary"] = dark ? Parse("#B9C0C9") : Parse("#475360");
        palette["TextTertiary"] = dark ? Parse("#A2ABB7") : Parse("#52606D");
        palette["TextDisabled"] = Mix(background, text, 0.45);
        palette["IconDefault"] = palette["TextSecondary"];
        palette["IconHover"] = text;
        palette["IconAccent"] = accent;
        palette["MarkdownInlineCodeBackground"] = Mix(background, accent, 0.18);
        palette["MarkdownInlineCodeSelection"] = Mix(background, accent, 0.30);
        palette["MarkdownCodeBlockBackground"] = Mix(background, text, 0.035);
        palette["MarkdownCodeBlockBorder"] = palette["BorderHover"];
        palette["MarkdownQuoteBackground"] = Mix(background, accent, 0.07);
        palette["MarkdownQuoteBorder"] = palette["AccentMuted"];
        palette["MarkdownTableBorder"] = palette["BorderEmphasis"];
        palette["MarkdownTableDivider"] = palette["BorderHover"];
        palette["MarkdownTableHeader"] = Mix(background, accent, 0.10);

        foreach ((string key, WpfColor color) in palette)
        {
            resources[key] = color;
            resources[key + "Brush"] = new SolidColorBrush(color);
        }
        resources["TextSelectionBrush"] = new SolidColorBrush(palette["AccentSubtlePressed"]);
        resources["TextOnAccentBrush"] = new SolidColorBrush(dark ? Parse("#1E252E") : Parse("#FFFFFF"));
    }

    private static WpfColor Parse(string value) => (WpfColor)WpfColorConverter.ConvertFromString(value)!;

    private static WpfColor Mix(WpfColor first, WpfColor second, double amount) => WpfColor.FromRgb(
        (byte)Math.Round(first.R + (second.R - first.R) * amount),
        (byte)Math.Round(first.G + (second.G - first.G) * amount),
        (byte)Math.Round(first.B + (second.B - first.B) * amount));

    private static WpfColor EnsureContrast(WpfColor color, WpfColor background, WpfColor text)
    {
        for (int step = 0; step <= 20; step++)
        {
            WpfColor candidate = Mix(color, text, step / 20d);
            double first = Luminance(candidate);
            double second = Luminance(background);
            // Leave room for tinted hover/selection surfaces and pressed buttons.
            if ((Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05) >= 5.5)
            {
                return candidate;
            }
        }
        return text;
    }

    private static double Luminance(WpfColor color)
    {
        static double Linear(byte value)
        {
            double channel = value / 255d;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
}

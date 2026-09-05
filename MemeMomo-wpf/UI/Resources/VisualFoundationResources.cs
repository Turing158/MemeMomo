using System.Windows;

namespace MemeMomo.UI.Resources;

public static class VisualFoundationResources
{
    public static readonly IReadOnlyList<string> ExpectedKeys =
    [
        "MotionFastDuration", "MotionStandardDuration", "MotionDockDuration",
        "BgPrimary", "BgSecondary", "BgTertiary", "BgHover", "SurfacePrimary", "SurfaceHover", "SurfaceActive",
        "BorderDefault", "BorderHover", "BorderSubtle", "BorderEmphasis", "BorderFocus",
        "AccentPrimary", "AccentHover", "AccentMuted", "AccentSubtle", "AccentSubtlePressed", "AccentPressed",
        "MarkdownInlineCodeBackground", "MarkdownInlineCodeSelection",
        "DangerPrimary", "DangerHover", "DangerSubtle", "SuccessPrimary", "SuccessHover", "SuccessPressed", "SuccessSubtle",
        "TextPrimary", "TextSecondary", "TextTertiary", "TextDisabled", "IconDefault", "IconHover", "IconAccent",
        "MarkdownCodeBlockBackground", "MarkdownCodeBlockBorder", "MarkdownQuoteBackground", "MarkdownQuoteBorder",
        "MarkdownTableBorder", "MarkdownTableDivider", "MarkdownTableHeader",
        "BgPrimaryBrush", "BgSecondaryBrush", "BgTertiaryBrush", "BgHoverBrush", "TransparentBrush",
        "SurfacePrimaryBrush", "SurfaceHoverBrush", "SurfaceActiveBrush", "BorderDefaultBrush", "BorderHoverBrush",
        "BorderSubtleBrush", "BorderEmphasisBrush", "BorderFocusBrush", "AccentPrimaryBrush", "AccentHoverBrush",
        "AccentMutedBrush", "AccentSubtleBrush", "AccentSubtlePressedBrush", "AccentPressedBrush", "TextSelectionBrush",
        "MarkdownInlineCodeBackgroundBrush", "MarkdownInlineCodeSelectionBrush",
        "DangerPrimaryBrush", "DangerHoverBrush", "DangerSubtleBrush", "SuccessPrimaryBrush", "SuccessHoverBrush",
        "SuccessPressedBrush", "SuccessSubtleBrush", "TextPrimaryBrush", "TextSecondaryBrush", "TextTertiaryBrush",
        "TextDisabledBrush", "IconDefaultBrush", "IconHoverBrush", "IconAccentBrush", "TextOnAccentBrush",
        "MarkdownCodeBlockBackgroundBrush", "MarkdownCodeBlockBorderBrush", "MarkdownQuoteBackgroundBrush",
        "MarkdownQuoteBorderBrush", "MarkdownTableBorderBrush", "MarkdownTableDividerBrush", "MarkdownTableHeaderBrush",
        "RadiusSm", "RadiusMd", "RadiusLg", "RadiusXl", "RadiusFull",
        "SettingsIcon", "PinIcon", "MinimizeIcon", "CloseIcon", "DeleteIcon", "DeleteXIcon", "SearchIcon",
        "ChevronDownIcon", "MemeMomoIcon", "MarkdownToolbarMenuCheckIcon", "MarkdownTableEdgeButtonTheme",
        "MarkdownTableEdgeMenuPresenterTheme", "MarkdownTableEdgeMenuItemTheme", "MarkdownToolbarMenuPresenterTheme",
        "MarkdownTablePickerPresenterTheme", "MarkdownToolbarMenuItemTheme", "MarkdownToolbarMenuSeparatorTheme"
    ];

    public static void EnsureLoaded(System.Windows.Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (FindResourceDictionary(application.Resources, "Foundation.xaml") is not null)
        {
            return;
        }

        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/MemeMomo;component/Resources/Foundation.xaml", UriKind.RelativeOrAbsolute)
        });
    }

    internal static object? FindResource(System.Windows.Application application, string key) =>
        application.TryFindResource(key);

    internal static void SetResource(System.Windows.Application application, string key, object value)
    {
        ResourceDictionary? owner = FindOwner(application.Resources, key);
        if (owner is null)
        {
            application.Resources[key] = value;
        }
        else
        {
            owner[key] = value;
        }
    }

    private static ResourceDictionary? FindOwner(ResourceDictionary dictionary, string key)
    {
        if (dictionary.Contains(key))
        {
            return dictionary;
        }

        foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
        {
            ResourceDictionary? owner = FindOwner(merged, key);
            if (owner is not null)
            {
                return owner;
            }
        }

        return null;
    }

    private static ResourceDictionary? FindResourceDictionary(ResourceDictionary dictionary, string fileName)
    {
        if (dictionary.Source?.OriginalString.EndsWith(fileName, StringComparison.OrdinalIgnoreCase) == true)
        {
            return dictionary;
        }

        foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
        {
            ResourceDictionary? found = FindResourceDictionary(merged, fileName);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}

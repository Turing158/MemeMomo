using System.Windows;
using System.Windows.Media;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using Microsoft.Win32;
using MemeMomo.Models;
using MemeMomo.Infrastructure;
using MemeMomo.UI.Resources;

namespace MemeMomo.UI;

internal interface ISystemThemeProbe : IDisposable
{
    event EventHandler? Changed;
    bool IsDark { get; }
}

internal sealed class WindowsThemeProbe : ISystemThemeProbe
{
    private int _disposed;
    public event EventHandler? Changed;
    public bool IsDark => ReadIsDark();

    internal WindowsThemeProbe() => SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            Changed = null;
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool ReadIsDark()
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            return value is int intValue && intValue == 0;
        }
        catch
        {
            return false;
        }
    }
}

internal static class ThemePreferences
{
    private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
    {
        ["BgPrimary"] = "#FFF6F3EC",
        ["BgSecondary"] = "#FFFAF8F3",
        ["BgTertiary"] = "#FFEFEAE1",
        ["BgHover"] = "#FFEBE5DA",
        ["SurfacePrimary"] = "#FFFFFFFF",
        ["SurfaceHover"] = "#FFFBF9F4",
        ["SurfaceActive"] = "#FFF2EDE4",
        ["BorderDefault"] = "#FFE5DDD1",
        ["BorderHover"] = "#FFD6CDBE",
        ["BorderSubtle"] = "#FFEDE8DD",
        ["BorderEmphasis"] = "#FFCDBBA8",
        ["BorderFocus"] = "#FFC97B5A",
        ["AccentPrimary"] = "#FFC06A48",
        ["AccentHover"] = "#FFA8583A",
        ["AccentMuted"] = "#FFE8C4A8",
        ["AccentSubtle"] = "#FFFAEDE4",
        ["AccentSubtlePressed"] = "#FFDDAE8E",
        ["AccentPressed"] = "#FF964E32",
        ["MarkdownInlineCodeBackground"] = "#FFE8C4A8",
        ["MarkdownInlineCodeSelection"] = "#FFC98261",
        ["DangerPrimary"] = "#FFC5543D",
        ["DangerHover"] = "#FFA8432E",
        ["DangerSubtle"] = "#FFFBE8E3",
        ["SuccessPrimary"] = "#FF4E8B6F",
        ["SuccessHover"] = "#FF3F765D",
        ["SuccessPressed"] = "#FF315F4A",
        ["SuccessSubtle"] = "#FFE4F0EA",
        ["TextPrimary"] = "#FF1E1A16",
        ["TextSecondary"] = "#FF5C554D",
        ["TextTertiary"] = "#FF8A8278",
        ["TextDisabled"] = "#FFB0A79B",
        ["IconDefault"] = "#FF6B6359",
        ["IconHover"] = "#FF1E1A16",
        ["IconAccent"] = "#FFC06A48",
        ["MarkdownCodeBlockBackground"] = "#FFE2E7EA",
        ["MarkdownCodeBlockBorder"] = "#FF818E98",
        ["MarkdownQuoteBackground"] = "#FFF0DFD4",
        ["MarkdownQuoteBorder"] = "#FFD2AA92",
        ["MarkdownTableBorder"] = "#FF8B7D6A",
        ["MarkdownTableDivider"] = "#FF978A79",
        ["MarkdownTableHeader"] = "#14000000"
    };

    private static readonly IReadOnlyDictionary<string, string> DarkPalette = new Dictionary<string, string>
    {
        ["BgPrimary"] = "#FF202225",
        ["BgSecondary"] = "#FF25272B",
        ["BgTertiary"] = "#FF30343A",
        ["BgHover"] = "#FF353A41",
        ["SurfacePrimary"] = "#FF292C31",
        ["SurfaceHover"] = "#FF30343A",
        ["SurfaceActive"] = "#FF393F47",
        ["BorderDefault"] = "#FF414750",
        ["BorderHover"] = "#FF535D6A",
        ["BorderSubtle"] = "#FF353A42",
        ["BorderEmphasis"] = "#FF657285",
        ["BorderFocus"] = "#FF9BB9DC",
        ["AccentPrimary"] = "#FF8FAFD5",
        ["AccentHover"] = "#FFA5C0E2",
        ["AccentMuted"] = "#FF506B8C",
        ["AccentSubtle"] = "#FF2B3746",
        ["AccentSubtlePressed"] = "#FF394D65",
        ["AccentPressed"] = "#FF799AC3",
        ["MarkdownInlineCodeBackground"] = "#FF394D65",
        ["MarkdownInlineCodeSelection"] = "#FF4B6789",
        ["DangerPrimary"] = "#FFD0928B",
        ["DangerHover"] = "#FFDEAAA4",
        ["DangerSubtle"] = "#FF3D3033",
        ["SuccessPrimary"] = "#FF8DB59F",
        ["SuccessHover"] = "#FFA2C7B2",
        ["SuccessPressed"] = "#FF77A08A",
        ["SuccessSubtle"] = "#FF2D3A35",
        ["TextPrimary"] = "#FFE1E4E8",
        ["TextSecondary"] = "#FFB9C0C9",
        ["TextTertiary"] = "#FFA2ABB7",
        ["TextDisabled"] = "#FF68717D",
        ["IconDefault"] = "#FFB0BAC6",
        ["IconHover"] = "#FFE1E4E8",
        ["IconAccent"] = "#FF8FAFD5",
        ["MarkdownCodeBlockBackground"] = "#FF252D36",
        ["MarkdownCodeBlockBorder"] = "#FF56677B",
        ["MarkdownQuoteBackground"] = "#FF293442",
        ["MarkdownQuoteBorder"] = "#FF5C7798",
        ["MarkdownTableBorder"] = "#FF667384",
        ["MarkdownTableDivider"] = "#FF4C5868",
        ["MarkdownTableHeader"] = "#148FAFD5"
    };

    private static System.Windows.Application? _application;
    private static ISystemThemeProbe? _probe;
    private static IDisposable? _probeLease;
    private static bool _dark;

    internal static event EventHandler? PaletteChanged;
    internal static ThemeMode Mode { get; private set; } = ThemeMode.FollowSystem;
    internal static bool IsDark => _dark;

    internal static IReadOnlyDictionary<string, string> GetPalette(bool dark) => dark ? DarkPalette : LightPalette;

    internal static void Initialize(System.Windows.Application application, ISystemThemeProbe? probe = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        Shutdown();
        VisualFoundationResources.EnsureLoaded(application);
        _application = application;
        _probe = probe ?? new WindowsThemeProbe();
        _probe.Changed += OnSystemThemeChanged;
        _probeLease = UiResourceTracker.Acquire(UiResourceKind.SystemEventsSubscription);
        ApplyMode(Mode);
    }

    internal static void ApplyMode(ThemeMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            mode = ThemeMode.FollowSystem;
        }

        if (_application is null)
        {
            Mode = mode;
            return;
        }

        if (!_application.Dispatcher.CheckAccess())
        {
            _application.Dispatcher.BeginInvoke(() => ApplyMode(mode));
            return;
        }

        Mode = mode;
        ApplyPalette(mode == ThemeMode.Dark || mode == ThemeMode.FollowSystem && (_probe?.IsDark ?? false));
    }

    internal static void Shutdown()
    {
        if (_probe is not null)
        {
            _probe.Changed -= OnSystemThemeChanged;
            _probe.Dispose();
        }

        _probe = null;
        _probeLease?.Dispose();
        _probeLease = null;
        _application = null;
        PaletteChanged = null;
    }

    private static void OnSystemThemeChanged(object? sender, EventArgs e)
    {
        if (Mode != ThemeMode.FollowSystem || _application is null)
        {
            return;
        }

        _application.Dispatcher.BeginInvoke(() => ApplyMode(Mode));
    }

    private static void ApplyPalette(bool dark)
    {
        if (_application is null)
        {
            return;
        }

        IReadOnlyDictionary<string, string> palette = dark ? DarkPalette : LightPalette;
        _dark = dark;
        foreach ((string key, string value) in palette)
        {
            WpfColor color = (WpfColor)WpfColorConverter.ConvertFromString(value)!;
            VisualFoundationResources.SetResource(_application, key, color);
            SetBrushColor(key + "Brush", color);
        }

        SetBrushColor("TransparentBrush", dark ? WpfColor.FromArgb(0, 0, 0, 0) : WpfColor.FromArgb(0, 255, 255, 255));
        SetBrushColor("TextSelectionBrush", (WpfColor)WpfColorConverter.ConvertFromString(palette["AccentSubtlePressed"])!);
        SetBrushColor("TextOnAccentBrush", dark ? (WpfColor)WpfColorConverter.ConvertFromString("#FF1E252E")! : Colors.White);

        PaletteChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void SetBrushColor(string key, WpfColor color)
    {
        if (_application is null || VisualFoundationResources.FindResource(_application, key) is not SolidColorBrush brush)
        {
            return;
        }

        if (brush.IsFrozen || brush.IsSealed)
        {
            SolidColorBrush replacement = brush.Clone();
            replacement.Color = color;
            VisualFoundationResources.SetResource(_application, key, replacement);
        }
        else
        {
            brush.Color = color;
        }
    }
}

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
        ["BgPrimary"] = "#FF181917",
        ["BgSecondary"] = "#FF1D1F1C",
        ["BgTertiary"] = "#FF282A26",
        ["BgHover"] = "#FF2D302B",
        ["SurfacePrimary"] = "#FF222420",
        ["SurfaceHover"] = "#FF282B26",
        ["SurfaceActive"] = "#FF31342E",
        ["BorderDefault"] = "#FF3B3F38",
        ["BorderHover"] = "#FF4B5147",
        ["BorderSubtle"] = "#FF30342E",
        ["BorderEmphasis"] = "#FF596052",
        ["BorderFocus"] = "#FFD69A78",
        ["AccentPrimary"] = "#FFD58B68",
        ["AccentHover"] = "#FFE1A080",
        ["AccentMuted"] = "#FF98664F",
        ["AccentSubtle"] = "#FF3A2B24",
        ["AccentSubtlePressed"] = "#FF573C30",
        ["AccentPressed"] = "#FFB87355",
        ["MarkdownInlineCodeBackground"] = "#FF98664F",
        ["MarkdownInlineCodeSelection"] = "#FF6A4637",
        ["DangerPrimary"] = "#FFD87361",
        ["DangerHover"] = "#FFE58A79",
        ["DangerSubtle"] = "#FF3D2926",
        ["SuccessPrimary"] = "#FF72AD8F",
        ["SuccessHover"] = "#FF86C2A2",
        ["SuccessPressed"] = "#FF579174",
        ["SuccessSubtle"] = "#FF22352C",
        ["TextPrimary"] = "#FFF0ECE3",
        ["TextSecondary"] = "#FFC8C1B6",
        ["TextTertiary"] = "#FFA49D92",
        ["TextDisabled"] = "#FF746F67",
        ["IconDefault"] = "#FFBAB2A6",
        ["IconHover"] = "#FFF0ECE3",
        ["IconAccent"] = "#FFD58B68",
        ["MarkdownCodeBlockBackground"] = "#FF252A2D",
        ["MarkdownCodeBlockBorder"] = "#FF65717A",
        ["MarkdownQuoteBackground"] = "#FF352A24",
        ["MarkdownQuoteBorder"] = "#FF765B4C",
        ["MarkdownTableBorder"] = "#FF7D8376",
        ["MarkdownTableDivider"] = "#FF62695F",
        ["MarkdownTableHeader"] = "#14FFFFFF"
    };

    private static System.Windows.Application? _application;
    private static ISystemThemeProbe? _probe;
    private static IDisposable? _probeLease;
    private static bool _dark;

    internal static event EventHandler? PaletteChanged;
    internal static ThemeMode Mode { get; private set; } = ThemeMode.FollowSystem;
    internal static bool IsDark => _dark;

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
        SetBrushColor("TextOnAccentBrush", dark ? (WpfColor)WpfColorConverter.ConvertFromString("#FF211914")! : Colors.White);

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

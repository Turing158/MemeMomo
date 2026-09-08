using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using MemeMomo.Models;

namespace MemeMomo.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly Dictionary<string, string[]> Translations = LoadTranslations();

    private LocalizationService() { }

    public static LocalizationService Current { get; } = new();
    public static AppLanguage CurrentLanguage { get; private set; } = AppLanguage.ChineseSimplified;
    public AppLanguage Language => CurrentLanguage;
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(CurrentLanguage switch
    {
        AppLanguage.English => "en-US",
        AppLanguage.ChineseTraditional => "zh-TW",
        _ => "zh-CN",
    });

    public event PropertyChangedEventHandler? PropertyChanged;
    public static event EventHandler? LanguageChanged;
    internal static IEnumerable<string> Sources => Translations.Keys;

    public static string Get(string source) => Get(source, CurrentLanguage);

    internal static string Get(string source, AppLanguage language) =>
        language != AppLanguage.ChineseSimplified && Translations.TryGetValue(source, out string[]? values)
            ? values[language == AppLanguage.English ? 0 : 1]
            : source;

    public static string Format(string source, params object?[] arguments) =>
        string.Format(Culture, Get(source), arguments);

    public static void SetLanguage(AppLanguage language)
    {
        language = Enum.IsDefined(language) ? language : AppLanguage.ChineseSimplified;
        bool changed = language != CurrentLanguage;
        CurrentLanguage = language;
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        if (!changed) return;

        Current.PropertyChanged?.Invoke(Current, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    private static Dictionary<string, string[]> LoadTranslations()
    {
        using Stream stream = typeof(LocalizationService).Assembly.GetManifestResourceStream("MemeMomo.Resources.Strings.json")
            ?? throw new InvalidOperationException("The UI translation catalog is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)
            ?? throw new InvalidOperationException("The UI translation catalog is empty.");
    }
}

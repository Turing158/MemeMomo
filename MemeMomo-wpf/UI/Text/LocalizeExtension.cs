using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using MemeMomo.Services;
using Binding = System.Windows.Data.Binding;

namespace MemeMomo.UI.Text;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocalizeExtension(string source) : MarkupExtension
{
    [ConstructorArgument("source")]
    public string Source { get; set; } = source;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        CreateBinding(Source, []).ProvideValue(serviceProvider);

    public static void Set(DependencyObject target, DependencyProperty property, string source, params object?[] arguments) =>
        BindingOperations.SetBinding(target, property, CreateBinding(source, arguments));

    private static Binding CreateBinding(string source, object?[] arguments) => new(nameof(LocalizationService.Language))
    {
        Source = LocalizationService.Current,
        Mode = BindingMode.OneWay,
        Converter = new TranslationConverter(source, arguments),
    };

    private sealed class TranslationConverter(string source, object?[] arguments) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            arguments.Length == 0 ? LocalizationService.Get(source) : LocalizationService.Format(source, arguments);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}

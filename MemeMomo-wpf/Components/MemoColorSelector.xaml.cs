using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.UI;
using MemeMomo.UI.Animation;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace MemeMomo.Components;

public partial class MemoColorSelector : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty SelectedPresetProperty = DependencyProperty.Register(
        nameof(SelectedPreset), typeof(MemoColorPreset), typeof(MemoColorSelector),
        new FrameworkPropertyMetadata(MemoColorPreset.FollowTheme,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedPresetChanged));

    private bool _refreshing;

    public MemoColorSelector()
    {
        InitializeComponent();
        RefreshOptions();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public MemoColorPreset SelectedPreset
    {
        get => (MemoColorPreset)GetValue(SelectedPresetProperty);
        set => SetValue(SelectedPresetProperty, value);
    }

    public event EventHandler? SelectionChanged;

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (e.Handled || VisualTreeHelper.GetParent(this) is not UIElement parent)
        {
            return;
        }

        // The non-scrolling ListBox still consumes wheel input. Let the containing
        // settings page handle it once, without changing selection or focus.
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemePreferences.PaletteChanged += OnAppearanceChanged;
        LocalizationService.LanguageChanged += OnAppearanceChanged;
        RefreshOptions();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ThemePreferences.PaletteChanged -= OnAppearanceChanged;
        LocalizationService.LanguageChanged -= OnAppearanceChanged;
    }

    private void OnAppearanceChanged(object? sender, EventArgs e) => RefreshOptions();

    private void RefreshOptions()
    {
        if (Choices is null) return;
        _refreshing = true;
        try
        {
            if (Choices.ItemsSource is not ColorChoice[] choices)
            {
                Choices.ItemsSource = MemoWindowPalette.Options
                    .Select(option => new ColorChoice(option.Preset, option.Name)).ToArray();
            }
            else
            {
                // Applying a preference also reapplies the theme. Keep containers,
                // focus and in-flight hover/selection animations intact.
                foreach (ColorChoice choice in choices) choice.Refresh();
            }
            Choices.SelectedValue = MemoWindowPalette.Normalize(SelectedPreset);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static void OnSelectedPresetChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        MemoColorSelector selector = (MemoColorSelector)owner;
        selector.Choices.SelectedValue = MemoWindowPalette.Normalize((MemoColorPreset)e.NewValue);
        selector.SelectionChanged?.Invoke(selector, EventArgs.Empty);
    }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_refreshing && Choices.SelectedValue is MemoColorPreset preset)
        {
            SetCurrentValue(SelectedPresetProperty, preset);
        }
    }

    private void OnChoiceLoaded(object sender, RoutedEventArgs e) =>
        UpdateChoiceSelection((ListBoxItem)sender, animate: false);

    private void OnChoiceSelectionChanged(object sender, RoutedEventArgs e) =>
        UpdateChoiceSelection((ListBoxItem)sender, animate: true);

    private void OnChoiceUnloaded(object sender, RoutedEventArgs e)
    {
        ListBoxItem choice = (ListBoxItem)sender;
        if (choice.Template?.FindName("ChoiceSelectedLayer", choice) is FrameworkElement layer)
        {
            MotionAnimations.Cancel(layer);
        }
    }

    private static void UpdateChoiceSelection(ListBoxItem choice, bool animate)
    {
        if (choice.Template?.FindName("ChoiceSelectedLayer", choice) is not FrameworkElement layer
            || choice.Template.FindName("SelectedMark", choice) is not FrameworkElement mark
            || mark.RenderTransform is not ScaleTransform scale)
        {
            return;
        }

        if (scale.IsFrozen)
        {
            scale = scale.Clone();
            mark.RenderTransform = scale;
        }

        double from = layer.Opacity;
        double target = choice.IsSelected ? 1 : 0;
        void Apply(double progress)
        {
            double opacity = from + (target - from) * progress;
            layer.Opacity = mark.Opacity = opacity;
            scale.ScaleX = scale.ScaleY = 0.7 + 0.3 * opacity;
        }

        MotionAnimations.Cancel(layer);
        if (!animate || !choice.IsLoaded || Math.Abs(from - target) < 0.001)
        {
            Apply(1);
            return;
        }

        MotionAnimations.Start(layer, MotionPreferences.StandardDuration, MotionEasing.CubicEaseOut, Apply);
    }

    private sealed class ColorChoice(MemoColorPreset preset, string labelKey) : INotifyPropertyChanged
    {
        public MemoColorPreset Preset { get; } = preset;
        public string Label => LocalizationService.Get(labelKey);
        public SolidColorBrush Swatch { get; } = new(MemoWindowPalette.Background(preset));
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Refresh()
        {
            Color color = MemoWindowPalette.Background(Preset);
            if (Swatch.Color != color) Swatch.Color = color;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }
}

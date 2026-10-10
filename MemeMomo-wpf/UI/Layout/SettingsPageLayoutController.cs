using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;

namespace MemeMomo.UI.Layout;

/// <summary>Only the settings body controls its navigation breakpoint; the workspace is independent.</summary>
internal sealed class SettingsPageLayoutController : IDisposable
{
    internal const double WideThreshold = 680;
    internal const double NavigationWidth = 136;
    internal const double NavigationGap = 16;
    internal const double FormMaximumWidth = 800;
    private readonly Grid _body;
    private readonly ListBox _navigation;
    private readonly ComboBox _selector;
    private readonly FrameworkElement _viewport;
    private readonly Action _layoutChanged;
    private bool? _wide;
    private bool _disposed;

    internal SettingsPageLayoutController(Grid body, ListBox navigation, ComboBox selector,
        FrameworkElement viewport, Action layoutChanged)
    {
        _body = body;
        _navigation = navigation;
        _selector = selector;
        _viewport = viewport;
        _layoutChanged = layoutChanged;
        body.SizeChanged += OnSizeChanged;
    }

    internal void Refresh()
    {
        if (_disposed || _body.ActualWidth <= 0) return;
        bool wide = _body.ActualWidth >= WideThreshold;
        if (_wide != wide)
        {
            bool navigationFocused = _navigation.IsKeyboardFocusWithin || _selector.IsKeyboardFocusWithin;
            _selector.IsDropDownOpen = false;
            _wide = wide;
            _body.RowDefinitions.Clear();
            _body.ColumnDefinitions.Clear();
            if (wide)
            {
                _body.RowDefinitions.Add(new RowDefinition());
                _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NavigationWidth) });
                _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NavigationGap) });
                _body.ColumnDefinitions.Add(new ColumnDefinition());
            }
            else
            {
                _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _body.RowDefinitions.Add(new RowDefinition());
                _body.ColumnDefinitions.Add(new ColumnDefinition());
            }
            _navigation.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            _selector.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetRow(_viewport, wide ? 0 : 1);
            Grid.SetColumn(_viewport, wide ? 2 : 0);
            if (navigationFocused) (wide ? (UIElement)_navigation : _selector).Focus();
        }
        _layoutChanged();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Refresh();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _body.SizeChanged -= OnSizeChanged;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MemeMomo.UI.Animation;
using MemeMomo.UI.Text;

namespace MemeMomo.UI.Layout;

/// <summary>Repositions the existing editor and list without changing their sessions.</summary>
internal sealed class MainWindowLayoutController : IDisposable
{
    // All values are WPF DIP. The list minimum takes precedence over the 1:3 ratio.
    internal const double WideThreshold = 660;
    internal const double ListMinimum = 320;
    internal const double PaneGap = 12;

    private readonly Window _window;
    private readonly FrameworkElement _expandedSurface;
    private readonly Grid _content;
    private readonly FrameworkElement _editor;
    private readonly PaneViewport _listViewport;
    private readonly FrameworkElement _list;
    private readonly TextBlock _emptyHint;
    private readonly RotateTransform _emptyArrow;
    private readonly Func<bool> _isExpanded;
    private readonly Action _cancelTransientVisuals;
    private readonly PaneLayoutTransition _transition;
    private readonly FrameAnimation _listAnimation;
    private readonly TranslateTransform _listOffset = new();
    private DispatcherOperation? _pending;
    private bool? _wide;
    private bool _disposed;
    private bool _suppressTransition = true;
    private bool _workspaceActive = true;
    private bool _listCollapsed;
    private double _listReveal = 1;

    internal event Action? ListPaneStateChanged;
    internal bool IsListPaneCollapsed => _wide == true && _listCollapsed;
    internal bool CanToggleListPane => !_disposed && _workspaceActive && _wide == true
        && _window.IsLoaded && _window.IsVisible && _window.WindowState != WindowState.Minimized
        && _isExpanded() && _expandedSurface.IsHitTestVisible;

    internal MainWindowLayoutController(Window window, FrameworkElement expandedSurface, FrameworkElement viewport,
        System.Windows.Controls.Image snapshot, Grid content,
        FrameworkElement editor, PaneViewport listViewport, FrameworkElement list, TextBlock emptyHint, RotateTransform emptyArrow,
        Func<bool> isExpanded, Action cancelTransientVisuals, IAnimationFrameSource? frames = null,
        IAnimationFrameSource? listFrames = null)
    {
        _window = window;
        _expandedSurface = expandedSurface;
        _content = content;
        _editor = editor;
        _listViewport = listViewport;
        _list = list;
        _emptyHint = emptyHint;
        _emptyArrow = emptyArrow;
        _isExpanded = isExpanded;
        _cancelTransientVisuals = cancelTransientVisuals;
        _transition = new PaneLayoutTransition(content, viewport, snapshot, [editor, listViewport], frames);
        _listAnimation = new FrameAnimation(listFrames);
        _list.RenderTransform = _listOffset;
        _window.Loaded += OnLoaded;
        _window.Unloaded += OnUnloaded;
        _window.SizeChanged += OnSizeChanged;
        _window.IsVisibleChanged += OnVisibilityChanged;
        _expandedSurface.IsHitTestVisibleChanged += OnExpandedChanged;
        _content.SizeChanged += OnContentSizeChanged;
    }

    private void RequestLayout()
    {
        if (_disposed || !_workspaceActive || !_window.IsLoaded || !_window.IsVisible || _pending is not null) return;
        _pending = _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _pending = null;
            ApplyLayout();
        }));
    }

    private void ApplyLayout(bool cancelTransientVisuals = true)
    {
        if (_disposed || !_workspaceActive || !_window.IsLoaded || !_window.IsVisible || !_isExpanded()
            || !_expandedSurface.IsHitTestVisible || _window.WindowState == WindowState.Minimized
            || !double.IsFinite(_window.ActualWidth) || _window.ActualWidth <= 0) return;

        bool wide = _window.ActualWidth >= WideThreshold;
        bool animate = _wide.HasValue && !_suppressTransition && MotionPreferences.AnimationsEnabled;
        _suppressTransition = false;
        if (_wide == wide)
        {
            ListPaneStateChanged?.Invoke();
            return;
        }
        // Capture the current visual positions before resetting a transition, so
        // reversing at the breakpoint continues from the frame already on screen.
        var from = animate ? _transition.Capture() : null;
        _transition.Reset();
        _listAnimation.Cancel();
        if (cancelTransientVisuals) _cancelTransientVisuals();
        _wide = wide;
        _content.RowDefinitions.Clear();
        _content.ColumnDefinitions.Clear();
        if (wide)
        {
            _content.RowDefinitions.Add(new RowDefinition());
            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = ListMinimum });
            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PaneGap) });
            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        }
        else
        {
            _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _content.RowDefinitions.Add(new RowDefinition());
            _content.ColumnDefinitions.Add(new ColumnDefinition());
        }

        Grid.SetRow(_editor, 0);
        Grid.SetColumn(_editor, wide ? 2 : 0);
        Grid.SetRow(_listViewport, wide ? 0 : 1);
        Grid.SetColumn(_listViewport, 0);
        _editor.MaxHeight = wide ? double.PositiveInfinity : 230;
        _editor.Margin = wide ? new Thickness(0) : new Thickness(0, 0, 0, 10);
        KeyboardNavigation.SetTabIndex(_editor, wide ? 1 : 0);
        KeyboardNavigation.SetTabIndex(_listViewport, wide ? 0 : 1);
        LocalizeExtension.Set(_emptyHint, TextBlock.TextProperty, wide
            ? "在右侧写下第一条 Markdown 备忘录\n按 Ctrl+Enter 新增"
            : "在上方写下第一条 Markdown 备忘录\n按 Ctrl+Enter 新增");
        _emptyArrow.Angle = wide ? -90 : 180;
        ApplyListReveal(wide && _listCollapsed ? 0 : 1);
        ListPaneStateChanged?.Invoke();
        if (from is not null)
        {
            _content.UpdateLayout();
            _transition.AnimateFrom(from);
        }
    }

    internal void ToggleListPane()
    {
        if (!CanToggleListPane) return;
        _transition.Reset();
        _cancelTransientVisuals();
        _listCollapsed = !_listCollapsed;
        double from = _listReveal;
        double target = _listCollapsed ? 0 : 1;
        // Start cancels the previous channel while retaining its current reveal.
        _listAnimation.Start(TimeSpan.FromMilliseconds(240), MotionEasing.CubicEaseOut,
            progress => ApplyListReveal(from + (target - from) * progress));
        ListPaneStateChanged?.Invoke();
    }

    private void ApplyListReveal(double reveal)
    {
        _listReveal = reveal;
        bool wide = _wide == true;
        bool collapsed = wide && _listCollapsed;
        _listViewport.IsEnabled = _listViewport.IsHitTestVisible = !collapsed;
        _list.Visibility = reveal == 0 ? Visibility.Hidden : Visibility.Visible;
        _list.Opacity = reveal;
        _listOffset.X = 0;
        if (!wide || reveal >= 1)
        {
            _listViewport.ContentWidth = double.NaN;
            if (!wide) return;
            _content.ColumnDefinitions[0].MinWidth = ListMinimum;
            _content.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            _content.ColumnDefinitions[1].Width = new GridLength(PaneGap);
            return;
        }

        // Keep the list at its expanded width while its viewport slides closed.
        // Only the editor reflows into the space being released by the list.
        double listWidth = Math.Max(ListMinimum, (_content.ActualWidth - PaneGap) / 4);
        _listViewport.ContentWidth = listWidth;
        _listOffset.X = -listWidth * (1 - reveal);
        _content.ColumnDefinitions[0].MinWidth = 0;
        _content.ColumnDefinitions[0].Width = new GridLength(listWidth * reveal);
        _content.ColumnDefinitions[1].Width = new GridLength(PaneGap * reveal);
    }

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_disposed && e.WidthChanged && _wide == true && _listReveal < 1)
            ApplyListReveal(_listReveal);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed) return;
        if (!_workspaceActive) { SuspendTransition(); return; }
        if (!_isExpanded() || !_expandedSurface.IsHitTestVisible || _window.WindowState == WindowState.Minimized)
            SuspendTransition();
        // Even a resize within one orientation invalidates card/drag coordinates.
        _cancelTransientVisuals();
        RequestLayout();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => RequestLayout();
    private void OnExpandedChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        SuspendTransition();
        RequestLayout();
    }
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_window.IsVisible) RequestLayout();
        else { CancelPending(); SuspendTransition(); }
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelPending();
        SuspendTransition();
    }
    internal void CompleteTransition()
    {
        _transition.Reset();
        _listAnimation.Cancel();
        ApplyListReveal(IsListPaneCollapsed ? 0 : 1);
    }
    internal void SetWorkspaceActive(bool active)
    {
        _workspaceActive = active;
        CancelPending();
        SuspendTransition();
        if (active) ApplyLayout(cancelTransientVisuals: false);
        ListPaneStateChanged?.Invoke();
    }
    private void SuspendTransition()
    {
        _suppressTransition = true;
        CompleteTransition();
        ListPaneStateChanged?.Invoke();
    }
    private void CancelPending()
    {
        _pending?.Abort();
        _pending = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelPending();
        CompleteTransition();
        _transition.Dispose();
        _listAnimation.Dispose();
        _window.Loaded -= OnLoaded;
        _window.Unloaded -= OnUnloaded;
        _window.SizeChanged -= OnSizeChanged;
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _expandedSurface.IsHitTestVisibleChanged -= OnExpandedChanged;
        _content.SizeChanged -= OnContentSizeChanged;
        ListPaneStateChanged = null;
    }
}

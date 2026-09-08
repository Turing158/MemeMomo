using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Data;
using System.Windows.Automation;
using MemeMomo.UI.Animation;
using MemeMomo.UI;
using MemeMomo.UI.Text;
using Button = System.Windows.Controls.Button;

namespace MemeMomo.UI.Windows;

public class BorderlessWindow : Window
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CornerRadius),
            typeof(CornerRadius),
            typeof(BorderlessWindow),
            new PropertyMetadata(new CornerRadius(14), OnShellMetricChanged));

    public static readonly DependencyProperty TitleBarHeightProperty =
        DependencyProperty.Register(
            nameof(TitleBarHeight),
            typeof(double),
            typeof(BorderlessWindow),
            new PropertyMetadata(48d, OnShellMetricChanged));

    public static readonly DependencyProperty TitleBarContentProperty =
        DependencyProperty.Register(nameof(TitleBarContent), typeof(object), typeof(BorderlessWindow));

    public static readonly DependencyProperty TitleBarContentTemplateProperty =
        DependencyProperty.Register(nameof(TitleBarContentTemplate), typeof(DataTemplate), typeof(BorderlessWindow));

    public static readonly DependencyProperty ShowMinimizeButtonProperty =
        DependencyProperty.Register(
            nameof(ShowMinimizeButton),
            typeof(bool),
            typeof(BorderlessWindow),
            new PropertyMetadata(true, OnShowMinimizeButtonChanged));

    public static readonly DependencyProperty UseBuiltInTitleBarProperty =
        DependencyProperty.Register(
            nameof(UseBuiltInTitleBar),
            typeof(bool),
            typeof(BorderlessWindow),
            new PropertyMetadata(true, OnUseBuiltInTitleBarChanged));

    public static readonly DependencyProperty ShellBackgroundProperty =
        DependencyProperty.Register(
            nameof(ShellBackground),
            typeof(System.Windows.Media.Brush),
            typeof(BorderlessWindow),
            new PropertyMetadata(null, OnShellBrushChanged));

    public static readonly DependencyProperty ShellBorderBrushProperty =
        DependencyProperty.Register(
            nameof(ShellBorderBrush),
            typeof(System.Windows.Media.Brush),
            typeof(BorderlessWindow),
            new PropertyMetadata(null, OnShellBrushChanged));

    public static readonly RoutedCommand CloseWindowCommand = new(nameof(CloseWindowCommand), typeof(BorderlessWindow));
    public static readonly RoutedCommand MinimizeWindowCommand = new(nameof(MinimizeWindowCommand), typeof(BorderlessWindow));
    public static readonly RoutedCommand TogglePinCommand = new(nameof(TogglePinCommand), typeof(BorderlessWindow));

    private readonly BorderlessWindowBehavior _behavior;
    private DwmWindowAdapter? _dwm;
    private WindowTransitionController? _transition;
    private WindowChrome? _chrome;
    private Button? _pinButton;
    private Button? _minimizeButton;
    private Button? _closeButton;
    private Border? _shell;
    private Border? _titleBar;
    private RowDefinition? _titleRow;
    private bool _customNativeRegionActive;
    private bool _suppressResizeHitTest;
    private bool _allowClose;
    private bool _closePending;
    private bool _shellBuilt;
    private int _disposed;

    public BorderlessWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        AllowsTransparency = UsePerPixelTransparency;
        if (UsePerPixelTransparency)
        {
            // 透明窗口的外形全部由 WPF 自绘（外壳 Border / 贴边标签层）。Window 默认
            // 背景是 SystemColors.WindowBrush（白），不清空会从圆角外、贴边弧外的
            // 透明区域露出白色底。
            Background = null;
        }
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        _behavior = new BorderlessWindowBehavior(this);
        CommandBindings.Add(new CommandBinding(CloseWindowCommand, (_, _) => CloseWithTransition()));
        CommandBindings.Add(new CommandBinding(MinimizeWindowCommand, (_, _) => WindowState = WindowState.Minimized));
        CommandBindings.Add(new CommandBinding(TogglePinCommand, (_, _) => Topmost = !Topmost));
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public double TitleBarHeight { get => (double)GetValue(TitleBarHeightProperty); set => SetValue(TitleBarHeightProperty, value); }
    public object? TitleBarContent { get => GetValue(TitleBarContentProperty); set => SetValue(TitleBarContentProperty, value); }
    public DataTemplate? TitleBarContentTemplate { get => (DataTemplate?)GetValue(TitleBarContentTemplateProperty); set => SetValue(TitleBarContentTemplateProperty, value); }
    public bool ShowMinimizeButton { get => (bool)GetValue(ShowMinimizeButtonProperty); set => SetValue(ShowMinimizeButtonProperty, value); }
    public bool UseBuiltInTitleBar { get => (bool)GetValue(UseBuiltInTitleBarProperty); set => SetValue(UseBuiltInTitleBarProperty, value); }
    public System.Windows.Media.Brush? ShellBackground { get => (System.Windows.Media.Brush?)GetValue(ShellBackgroundProperty); set => SetValue(ShellBackgroundProperty, value); }
    public System.Windows.Media.Brush? ShellBorderBrush { get => (System.Windows.Media.Brush?)GetValue(ShellBorderBrushProperty); set => SetValue(ShellBorderBrushProperty, value); }
    public bool IsPinned => Topmost;

    /// <summary>
    /// 该窗口是否走 WPF 逐像素透明（layered）渲染。必须在构造期确定：透明窗口的
    /// 外形（抗锯齿圆角、贴边 D 形）由 WPF 自绘，不依赖原生 DWM 圆角或
    /// SetWindowRgn 裁剪（region 为二值掩码，弧边必然出现阶梯锯齿）。
    ///
    /// The default is per-pixel transparency because the shared window transition
    /// animates the whole HWND from Opacity=0. A non-layered WPF HWND clears its
    /// composition surface with black while that opacity is zero, which produces
    /// a black flash before the themed shell fades in.
    /// </summary>
    protected virtual bool UsePerPixelTransparency => true;

    internal virtual WindowTransitionProfile TransitionProfile => WindowTransitionProfile.Default;

    /// <summary>Allows a derived window host to finish an approved close request.</summary>
    protected bool IsCloseApproved => _allowClose;

    /// <summary>Closes without re-entering the transition/request gate.</summary>
    protected void CloseImmediately()
    {
        _allowClose = true;
        Close();
    }

    public void CloseWithTransition()
    {
        if (_allowClose)
        {
            Close();
            return;
        }

        if (_transition is null)
        {
            _allowClose = true;
            Close();
            return;
        }

        _transition.CloseAfterTransition(() =>
        {
            _allowClose = true;
            Close();
        });
    }

    public void PrepareForOpen() => _transition?.PrepareOpen();

    /// <summary>Restores the shell to its fully visible, unscaled state.</summary>
    protected void ResetWindowTransition() => _transition?.Reset();

    public void PlayOpenTransition(Action? completed = null)
    {
        if (_transition is null)
        {
            completed?.Invoke();
            return;
        }

        _transition.PlayOpen(completed);
    }

    /// <summary>Plays the close transition, hides the HWND and prepares it for reuse.</summary>
    protected void HideWithTransition(Action? hidden = null)
    {
        void HideNow()
        {
            Hide();
            hidden?.Invoke();
            PrepareForOpen();
        }

        if (_transition is null)
        {
            HideNow();
            return;
        }

        _transition.CloseAfterTransition(HideNow);
    }

    protected bool IsWindowTransitioning => _transition?.IsTransitioning == true;

    protected void CancelWindowTransitionForInteraction()
    {
        _transition?.Cancel();
        _transition?.Reset();
    }

    protected void SetCustomNativeRegionActive(bool active)
    {
        _customNativeRegionActive = active;
        ApplyCustomNativeRegionState();
        _dwm?.SetCustomRegionActive(active);
    }

    /// <summary>
    /// 抑制原生 resize 命中（贴边小标签形态下原生 resize 边会吃掉点击，
    /// 需让 WM_NCHITTEST 全部回落到 HTCLIENT）。
    /// </summary>
    internal bool SuppressResizeHitTest
    {
        get => _suppressResizeHitTest;
        set => _suppressResizeHitTest = value;
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        if (!UsePerPixelTransparency)
        {
            // 透明窗口不接 DWM 适配：layered 表面上 DWM 圆角/边框属性无效，
            // 而 Win10 region 回退会重新引入二值裁剪。
            _dwm ??= new DwmWindowAdapter(this, CornerRadius.TopLeft);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            if (!_closePending)
            {
                _closePending = true;
                Dispatcher.BeginInvoke(() =>
                {
                    _closePending = false;
                    CloseWithTransition();
                });
            }

            return;
        }

        base.OnClosing(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        BuildShell();
        _chrome = new WindowChrome
        {
            CaptionHeight = TitleBarHeight,
            ResizeBorderThickness = new Thickness(8),
            // 逐像素透明窗口上 GlassFrameThickness=0 会让 DWM 把系统圆角直接切进
            // 窗口渲染表面（贴边 D 形平直侧方角因此出现 ~5px 小弧）；-1（sheet of
            // glass）让 DWM 不再参与表面裁剪，外形完全交给 WPF 自绘。
            GlassFrameThickness = new Thickness(UsePerPixelTransparency ? -1 : 0),
            UseAeroCaptionButtons = false
        };
        WindowChrome.SetWindowChrome(this, _chrome);
        if (UsePerPixelTransparency
            && PresentationSource.FromVisual(this) is HwndSource source)
        {
            // HwndTarget starts with a black clear color. Keep the transparent
            // transition endpoint truly transparent even when the shell has
            // not rendered its first frame yet.
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
        ApplyCustomNativeRegionState();
        ApplyBuiltInTitleBarState();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TopmostProperty)
        {
            UpdatePinState();
        }
    }

    private void BuildShell()
    {
        if (_shellBuilt)
        {
            return;
        }

        _shellBuilt = true;
        object? body = Content;
        base.Content = null;
        Grid shellGrid = new();
        _titleRow = new RowDefinition { Height = new GridLength(TitleBarHeight) };
        shellGrid.RowDefinitions.Add(_titleRow);
        shellGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _titleBar = new Border { Padding = new Thickness(16, 0, 8, 0) };
        _titleBar.SetResourceReference(Border.BackgroundProperty, "BgSecondaryBrush");
        WindowChrome.SetIsHitTestVisibleInChrome(_titleBar, false);
        DockPanel titleDock = new() { LastChildFill = true };
        StackPanel buttons = new() { Orientation = System.Windows.Controls.Orientation.Horizontal };
        _minimizeButton = CreateTitleButton("最小化", "MinimizeIcon", (_, _) => WindowState = WindowState.Minimized);
        _pinButton = CreateTitleButton("置顶", "PinIcon", (_, _) => Topmost = !Topmost);
        _closeButton = CreateTitleButton("关闭", "CloseIcon", (_, _) => CloseWithTransition(), danger: true);
        _minimizeButton.Visibility = ShowMinimizeButton ? Visibility.Visible : Visibility.Collapsed;
        buttons.Children.Add(_minimizeButton);
        buttons.Children.Add(_pinButton);
        buttons.Children.Add(_closeButton);
        DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Right);
        titleDock.Children.Add(buttons);
        TextBlock defaultTitle = new()
        {
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        defaultTitle.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Title)) { Source = this });
        defaultTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        UIElement titleContent = TitleBarContent switch
        {
            UIElement element => element,
            null => defaultTitle,
            _ => new ContentPresenter
            {
                Content = TitleBarContent,
                ContentTemplate = TitleBarContentTemplate
            }
        };
        titleDock.Children.Add(titleContent);
        _titleBar.Child = titleDock;
        Grid.SetRow(_titleBar, 0);
        shellGrid.Children.Add(_titleBar);

        UIElement content = body as UIElement ?? new ContentPresenter { Content = body };
        AdornerDecorator decorator = new() { Child = content };
        Grid.SetRow(decorator, 1);
        shellGrid.Children.Add(decorator);

        _shell = new Border
        {
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = shellGrid
        };
        ApplyShellBrushes();
        ApplyCustomNativeRegionState();
        base.Content = _shell;
        _transition = new WindowTransitionController(this, _shell, TransitionProfile);
        _transition.PrepareOpen();
        ApplyBuiltInTitleBarState();
        UpdatePinState();
    }

    private Button CreateTitleButton(string name, string iconKey, RoutedEventHandler click, bool danger = false)
    {
        Button button = new()
        {
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            Style = (Style)FindResource("TitleBarButtonStyle"),
            ToolTip = name
        };
        AutomationProperties.SetName(button, name);
        if (danger)
        {
            InteractionState.SetIsDanger(button, true);
        }

        Path icon = new()
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Data = (Geometry)FindResource(iconKey)
        };
        icon.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = button });
        button.Content = icon;
        LocalizeExtension.Set(button, ToolTipProperty, name);
        LocalizeExtension.Set(button, AutomationProperties.NameProperty, name);
        button.Click += click;
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        return button;
    }

    private void UpdatePinState() => InteractionState.SetIsPinActive((DependencyObject?)_pinButton ?? this, Topmost);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildShell();
        if (_transition?.IsCloseRequested != true && !IsWindowTransitioning)
        {
            PlayOpenTransition();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _transition?.Dispose();
            _transition = null;
            _behavior.Dispose();
            _dwm?.Dispose();
            _dwm = null;
            SourceInitialized -= OnSourceInitialized;
            Loaded -= OnLoaded;
            Closed -= OnClosed;
        }
    }

    private static void OnShellMetricChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        BorderlessWindow window = (BorderlessWindow)d;
        if (e.Property == CornerRadiusProperty && window._shell is not null)
        {
            // 不透明窗口的 CornerRadius 仅由 DwmWindowAdapter 消费（WPF 外壳保持方角）；
            // 透明窗口的圆角由 WPF 自绘，需要即时反映到外壳。
            window.ApplyShellShapeState();
        }
        else if (e.Property == TitleBarHeightProperty && window._titleRow is not null)
        {
            window._titleRow.Height = new GridLength(window.UseBuiltInTitleBar ? Math.Max(0, (double)e.NewValue) : 0);
            if (window._chrome is not null)
            {
                window._chrome.CaptionHeight = window.UseBuiltInTitleBar ? Math.Max(0, (double)e.NewValue) : 0;
            }
        }
    }

    private static void OnShowMinimizeButtonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        BorderlessWindow window = (BorderlessWindow)d;
        if (window._minimizeButton is not null)
        {
            window._minimizeButton.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void OnUseBuiltInTitleBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((BorderlessWindow)d).ApplyBuiltInTitleBarState();

    private static void OnShellBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((BorderlessWindow)d).ApplyShellBrushes();

    private void ApplyBuiltInTitleBarState()
    {
        if (_titleRow is not null)
        {
            _titleRow.Height = new GridLength(UseBuiltInTitleBar ? Math.Max(0, TitleBarHeight) : 0);
        }

        if (_titleBar is not null)
        {
            _titleBar.Visibility = UseBuiltInTitleBar ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_chrome is not null)
        {
            _chrome.CaptionHeight = UseBuiltInTitleBar ? Math.Max(0, TitleBarHeight) : 0;
        }
    }

    private void ApplyShellBrushes()
    {
        if (_shell is null)
        {
            return;
        }

        if (ShellBorderBrush is null)
        {
            _shell.SetResourceReference(Border.BorderBrushProperty, "BorderDefaultBrush");
        }
        else
        {
            _shell.BorderBrush = ShellBorderBrush;
        }

        ApplyShellShapeState();
    }

    private void ApplyCustomNativeRegionState()
    {
        if (_shell is null)
        {
            return;
        }

        ApplyShellShapeState();
        _dwm?.SetCustomRegionActive(_customNativeRegionActive);
    }

    /// <summary>
    /// 应用外壳绘制状态。不透明窗口的外形由原生 DWM/Win10 region 掌控，WPF 外壳保持方角
    /// （第二层 WPF 圆角会让窗口默认背景在四角露出楔形）；逐像素透明窗口由 WPF 自绘抗锯齿
    /// 圆角。贴边 chrome 激活且窗口透明时外壳完全不绘制：D 形标签是唯一可见面，圆弧外
    /// 像素保持透明并穿透点击。
    /// </summary>
    private void ApplyShellShapeState()
    {
        if (_shell is null)
        {
            return;
        }

        bool dockChromeActive = _customNativeRegionActive;
        if (dockChromeActive)
        {
            if (UsePerPixelTransparency)
            {
                _shell.Background = null;
            }
        }
        else
        {
            if (ShellBackground is null)
            {
                _shell.SetResourceReference(Border.BackgroundProperty, "BgPrimaryBrush");
            }
            else
            {
                _shell.Background = ShellBackground;
            }
        }

        _shell.CornerRadius = !dockChromeActive && UsePerPixelTransparency
            ? CornerRadius
            : new CornerRadius(0);
        _shell.BorderThickness = dockChromeActive ? new Thickness(0) : new Thickness(1);
        _shell.ClipToBounds = !dockChromeActive;
        if (_chrome is not null)
        {
            _chrome.ResizeBorderThickness = dockChromeActive
                ? new Thickness(0)
                : new Thickness(8);
        }
    }
}

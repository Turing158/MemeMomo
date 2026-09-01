using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Memo.Behaviors;
using Memo.Components;
using Memo.Infrastructure;
using Memo.Models;
using Memo.Platform.Windows;
using Memo.Services;
using Memo.UI;
using Memo.UI.Animation;
using Memo.UI.Windows;
using Memo.ViewModels;
using InputMouseEventArgs = System.Windows.Input.MouseEventArgs;
using InputKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfImage = System.Windows.Controls.Image;
using WpfButton = System.Windows.Controls.Button;
using WpfPoint = System.Windows.Point;
using WpfApplication = System.Windows.Application;

namespace Memo;

/// <summary>
/// Main memo workflow window. Drag reorder is hosted here while list/editor
/// behavior and the window chrome remain owned by this class.
/// </summary>
public partial class MainWindow : BorderlessWindow
{
    private readonly MainViewModel _viewModel;
    private readonly WindowContext _context;
    private IMainWindowHostActions _hostActions;
    private readonly AppSettings _settings;
    private readonly List<DeleteVisual> _deleteVisuals = [];
    private readonly List<FlipEntry> _flipEntries = [];
    private MemoItem? _displayedMemo;
    private string _newMemoDraft = string.Empty;
    private bool _loadedOnce;
    private bool _closeRequestInProgress;
    private bool _disposed;
    private bool _updatingMemoOverlayScrollBar;
    private int _deleteGeneration;
    private FrameAnimation? _deleteSlideAnimation;
    private DragReorderManager? _dragManager;
    private Thumb? _memoOverlayScrollBarThumb;
    private readonly DispatcherTimer _memoOverlayScrollBarCollapseTimer;
    private readonly IDisposable _memoOverlayScrollBarTimerLease;

    private sealed class DefaultHostActions : IMainWindowHostActions
    {
        public void OpenSettings(WindowContext context) { }
        public void MinimizeToTray(WindowContext context) => ((Window)context.Window).Hide();
        public void SetTaskbarIconVisible(WindowContext context, bool visible) => ((Window)context.Window).ShowInTaskbar = visible;
        public Task<CloseButtonAction?> AskCloseButtonAction(WindowContext context) => Task.FromResult<CloseButtonAction?>(CloseButtonAction.MinimizeToTray);
        public void ExitApplication(WindowContext context) => WpfApplication.Current?.Shutdown();
    }

    private sealed class DeleteVisual
    {
        internal required Canvas Layer { get; init; }
        internal required WpfImage Image { get; init; }
        internal required BitmapSource Bitmap { get; init; }
        internal required ScaleTransform Scale { get; init; }
        internal FrameAnimation? Animation { get; set; }
    }

    private sealed record FlipEntry(FrameworkElement Element, TranslateTransform Transform);

    public MainWindow() : this(new MainViewModel(), AppSettings.CreateDefault(), null, null) { }

    internal MainWindow(
        MainViewModel viewModel,
        AppSettings settings,
        IMainWindowHostActions? hostActions = null,
        Window? owner = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _hostActions = hostActions ?? new DefaultHostActions();
        _context = new WindowContext(this);
        DataContext = _viewModel;
        Owner = owner;
        InitializeComponent();
        _memoOverlayScrollBarCollapseTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _memoOverlayScrollBarCollapseTimer.Tick += OnMemoOverlayScrollBarCollapseTimerTick;
        _memoOverlayScrollBarTimerLease = UiResourceTracker.Acquire(UiResourceKind.DispatcherTimer);
        InitializeDockingInteraction();
        _dragManager = new DragReorderManager(
            MemoList,
            MemoScrollViewer,
            _viewModel,
            (memo, screenPoint) => MemoPopoutRequested?.Invoke(memo, screenPoint));
        _dragManager.Attach();
        Topmost = settings.MainWindowTopmost;
        ShowInTaskbar = settings.ShowMainWindowTaskbarIcon;
        _viewModel.MemoDeleted += OnMemoDeleted;
        _viewModel.MemosLoaded += OnMemosLoaded;
        MarkdownEditor.SaveRequestedAsync = SaveMarkdownAsync;
        MarkdownEditor.CancelRequestedAsync = CancelMarkdownAsync;
        MarkdownEditor.NewRequested += OnNewRequested;
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }

    public MainViewModel ViewModel => _viewModel;
    public MemoItem? DisplayedMemo => _displayedMemo;
    public bool IsLoadedOnce => _loadedOnce;
    public DragReorderManager? DragManager => _dragManager;
    public event Action<MemoItem, WpfPoint>? MemoPopoutRequested;

    public void TogglePinned()
    {
        Topmost = !Topmost;
        _settings.MainWindowTopmost = Topmost;
        PersistRuntimeWindowState();
    }

    private void UpdatePinIconVisual(bool isPinned)
    {
        if (PinIcon.RenderTransform is not RotateTransform rotation)
        {
            return;
        }

        double target = isPinned ? -45 : 0;
        double from = rotation.Angle;
        if (!MotionPreferences.AnimationsEnabled)
        {
            rotation.Angle = target;
            return;
        }

        MotionAnimations.Start(
            PinIcon,
            TimeSpan.FromMilliseconds(190),
            MotionEasing.CubicEaseOut,
            progress => rotation.Angle = from + ((target - from) * progress));
    }

    public void ShowExpandedWithTransition(bool focusInput = false)
    {
        CancelWindowTransitionForInteraction();
        bool needsOpenTransition = !IsVisible || WindowState == WindowState.Minimized;

        void CompleteOpen()
        {
            Activate();
            if (focusInput)
            {
                StartNewComposerAsync().ContinueWith(
                    _ => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(MarkdownEditor.FocusEditor)),
                    TaskScheduler.Default).Observe();
            }
        }

        if (DockState != DockState.Expanded)
        {
            WindowState = WindowState.Normal;
            UpdateTaskbarIconVisibility(windowWillBeVisible: true);
            ResetWindowTransition();
            if (!IsVisible)
            {
                Show();
            }

            RestoreExpandedWithTransition(CompleteOpen);
            return;
        }

        if (needsOpenTransition)
        {
            PrepareForOpen();
        }

        WindowState = WindowState.Normal;
        UpdateTaskbarIconVisibility(windowWillBeVisible: true);
        if (!IsVisible)
        {
            Show();
        }

        if (needsOpenTransition)
        {
            Activate();
            PlayOpenTransition(CompleteOpen);
            return;
        }

        CompleteOpen();
    }

    /// <summary>Global show-window hotkey and tray restore use the same path.</summary>
    public void ShowWithTransition() => ShowExpandedWithTransition();

    /// <summary>Hides the main window while keeping the explicit-shutdown app alive.</summary>
    public void HideToTrayWithTransition()
    {
        if (!IsVisible)
        {
            return;
        }

        HideWithTransition(() =>
        {
            WindowState = WindowState.Normal;
            UpdateTaskbarIconVisibility(windowWillBeVisible: false);
        });
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TopmostProperty && PinButton is not null)
        {
            InteractionState.SetIsPinActive(PinButton, Topmost);
            UpdatePinIconVisual(Topmost);
            _dockHandleController?.SetTopmost(Topmost);
        }
    }

    internal void CloseImmediatelyForTest() => CloseImmediately();

    public void ConfigureHostActions(IMainWindowHostActions hostActions)
    {
        ArgumentNullException.ThrowIfNull(hostActions);
        _hostActions = hostActions;
    }

    /// <summary>Applies a settings snapshot to the live main window.</summary>
    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.CopyTo(_settings);
        Topmost = _settings.MainWindowTopmost;
        UpdateTaskbarIconVisibility(IsVisible);
        ApplyDockEnabledSetting(_settings.MainWindowDockEnabled);
        ApplyDockSizeSetting(_settings.MainWindowDockSize, persist: false);
        InteractionState.SetIsPinActive(PinButton, Topmost);
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
        {
            return;
        }

        _loadedOnce = true;
        UpdateTaskbarIconVisibility(windowWillBeVisible: true);
        await _viewModel.LoadAsync();
        MarkdownEditor.BeginNew(_newMemoDraft);
        UpdateEditVisual();
    }

    private void UpdateTaskbarIconVisibility(bool windowWillBeVisible) =>
        TaskbarIconVisibility.SetVisible(
            this,
            windowWillBeVisible
                && DockState == DockState.Expanded
                && _settings.ShowMainWindowTaskbarIcon);

    private void OnMemosLoaded() => Dispatcher.BeginInvoke(
        DispatcherPriority.DataBind,
        new Action(() =>
        {
            UpdateEditVisual();
            UpdateMemoOverlayScrollBar();
        }));

    private void OnMemoOverlayScrollBarLoaded(object sender, RoutedEventArgs e)
    {
        MemoOverlayScrollBar.ApplyTemplate();
        _memoOverlayScrollBarThumb = FindVisualChildren<Thumb>(MemoOverlayScrollBar).FirstOrDefault();
        if (_memoOverlayScrollBarThumb is not null)
        {
            InteractionState.SetIsRevealed(_memoOverlayScrollBarThumb, false);
        }

        UpdateMemoOverlayScrollBar();
    }

    private void OnMemoScrollViewerScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateMemoOverlayScrollBar();

    private void OnMemoOverlayScrollBarValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingMemoOverlayScrollBar || _disposed || !MemoScrollViewer.IsLoaded)
        {
            return;
        }

        MemoScrollViewer.ScrollToVerticalOffset(e.NewValue);
    }

    private void OnMemoOverlayScrollBarPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        SetMemoOverlayScrollBarExpanded(true);
        _memoOverlayScrollBarCollapseTimer.Stop();
    }

    private void OnMemoOverlayScrollBarPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ScheduleMemoOverlayScrollBarCollapse();

    private void OnMemoOverlayScrollBarLostMouseCapture(object sender, InputMouseEventArgs e)
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            ScheduleMemoOverlayScrollBarCollapse();
        }
    }

    private void OnMemoOverlayScrollBarCollapseTimerTick(object? sender, EventArgs e)
    {
        _memoOverlayScrollBarCollapseTimer.Stop();
        SetMemoOverlayScrollBarExpanded(false);
    }

    private void ScheduleMemoOverlayScrollBarCollapse()
    {
        if (_disposed)
        {
            return;
        }

        _memoOverlayScrollBarCollapseTimer.Stop();
        _memoOverlayScrollBarCollapseTimer.Start();
    }

    private void SetMemoOverlayScrollBarExpanded(bool expanded)
    {
        if (_memoOverlayScrollBarThumb is null)
        {
            MemoOverlayScrollBar.ApplyTemplate();
            _memoOverlayScrollBarThumb = FindVisualChildren<Thumb>(MemoOverlayScrollBar).FirstOrDefault();
        }

        if (_memoOverlayScrollBarThumb is not null)
        {
            InteractionState.SetIsRevealed(_memoOverlayScrollBarThumb, expanded);
        }
    }

    private void UpdateMemoOverlayScrollBar()
    {
        if (!MemoOverlayScrollBar.IsLoaded)
        {
            return;
        }

        double scrollableHeight = Math.Max(0, MemoScrollViewer.ExtentHeight - MemoScrollViewer.ViewportHeight);
        _updatingMemoOverlayScrollBar = true;
        try
        {
            MemoOverlayScrollBar.Maximum = scrollableHeight;
            MemoOverlayScrollBar.ViewportSize = Math.Max(0, MemoScrollViewer.ViewportHeight);
            MemoOverlayScrollBar.LargeChange = Math.Max(1, MemoScrollViewer.ViewportHeight);
            MemoOverlayScrollBar.SmallChange = 32;
            MemoOverlayScrollBar.Value = Math.Clamp(MemoScrollViewer.VerticalOffset, 0, scrollableHeight);
            MemoOverlayScrollBar.Visibility = scrollableHeight > 0.5
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _updatingMemoOverlayScrollBar = false;
        }
    }

    private async Task<bool> SaveMarkdownAsync(MarkdownSaveRequest request)
    {
        if (request.IsNewMemo)
        {
            MemoItem? item = await _viewModel.AddItemAndSaveAsync(request.Markdown);
            if (item is null)
            {
                return false;
            }

            _newMemoDraft = string.Empty;
            return true;
        }

        if (_displayedMemo is null)
        {
            return false;
        }

        await _viewModel.UpdateItemAndSaveAsync(_displayedMemo.Id, request.Markdown);
        return true;
    }

    private async Task CancelMarkdownAsync(string restoreMarkdown)
    {
        if (_displayedMemo is not null)
        {
            await _viewModel.UpdateItemAndSaveAsync(_displayedMemo.Id, restoreMarkdown);
        }
    }

    private async void OnNewRequested(object? sender, EventArgs e) => await StartNewComposerAsync();

    private async Task StartNewComposerAsync()
    {
        if (!MarkdownEditor.IsNewMemo && !await MarkdownEditor.CompleteEditingAsync())
        {
            return;
        }

        if (MarkdownEditor.IsNewMemo)
        {
            _newMemoDraft = MarkdownEditor.Markdown;
        }

        if (_displayedMemo is not null)
        {
            MemoEditCoordinator.Shared.Release(_displayedMemo.Id, this);
        }

        _viewModel.EndEdit();
        SetDisplayedMemo(null);
        string draft = _newMemoDraft;
        _newMemoDraft = string.Empty;
        MarkdownEditor.BeginNew(draft);
        UpdateEditVisual();
    }

    private async void OnMemoDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<WpfButton>(source) is not null)
        {
            return;
        }

        if (sender is not FrameworkElement { DataContext: MemoItem item })
        {
            return;
        }

        e.Handled = true;
        await ToggleMemoEditAsync(item);
    }

    internal async Task ToggleMemoEditAsync(MemoItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!MarkdownEditor.IsNewMemo && ReferenceEquals(_displayedMemo, item))
        {
            await MarkdownEditor.CompleteEditingAsync();
            MemoEditCoordinator.Shared.Release(item.Id, this);
            _viewModel.EndEdit();
            SetDisplayedMemo(null);
            MarkdownEditor.BeginNew(_newMemoDraft);
            _newMemoDraft = string.Empty;
            UpdateEditVisual();
            return;
        }

        if (!MarkdownEditor.IsNewMemo && !await MarkdownEditor.CompleteEditingAsync())
        {
            return;
        }

        if (MarkdownEditor.IsNewMemo)
        {
            _newMemoDraft = MarkdownEditor.Markdown;
        }

        if (_displayedMemo is not null)
        {
            MemoEditCoordinator.Shared.Release(_displayedMemo.Id, this);
        }

        if (!await MemoEditCoordinator.Shared.AcquireAsync(item.Id, this, RelinquishActiveEditorAsync))
        {
            return;
        }

        SetDisplayedMemo(item);
        _viewModel.BeginEdit(item.Id);
        MarkdownEditor.BeginExistingEdit(item.Content);
        UpdateEditVisual();
    }

    private async Task<bool> RelinquishActiveEditorAsync()
    {
        if (MarkdownEditor.IsNewMemo)
        {
            return true;
        }

        if (!await MarkdownEditor.CompleteEditingAsync())
        {
            return false;
        }

        if (_displayedMemo is not null)
        {
            MemoEditCoordinator.Shared.Release(_displayedMemo.Id, this);
        }

        _viewModel.EndEdit();
        SetDisplayedMemo(null);
        MarkdownEditor.BeginNew();
        UpdateEditVisual();
        return true;
    }

    private void SetDisplayedMemo(MemoItem? memo)
    {
        if (ReferenceEquals(_displayedMemo, memo))
        {
            return;
        }

        if (_displayedMemo is not null)
        {
            _displayedMemo.PropertyChanged -= OnDisplayedMemoChanged;
        }

        _displayedMemo = memo;
        if (_displayedMemo is not null)
        {
            _displayedMemo.PropertyChanged += OnDisplayedMemoChanged;
        }
    }

    private void OnDisplayedMemoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MemoItem.Content) && sender is MemoItem memo && ReferenceEquals(memo, _displayedMemo))
        {
            MarkdownEditor.SetExternalMarkdown(memo.Content);
        }
    }

    private void OnMemoCardPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Single click is selection/input only. It never transfers editor ownership.
        if (sender is FrameworkElement element)
        {
            InteractionState.SetIsSelected(element, true);
        }

        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: MemoItem item })
        {
            if (e.OriginalSource is DependencyObject source && FindParent<WpfButton>(source) is not null)
            {
                return;
            }

            e.Handled = true;
            _ = ToggleMemoEditAsync(item);
        }
    }

    private void OnMemoCardMouseEnter(object sender, InputMouseEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            InteractionState.SetIsSelected(element, true);
            UpdateEditVisual();
        }
    }

    private void OnMemoCardMouseLeave(object sender, InputMouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MemoItem item } element && _viewModel.EditingId != item.Id)
        {
            InteractionState.SetIsSelected(element, false);
            if (FindVisualChildren<WpfButton>(element).FirstOrDefault(button => button.Name == "DeleteButton") is { } deleteButton)
            {
                InteractionState.SetIsRevealed(deleteButton, false);
            }
        }

        UpdateEditVisual();
    }

    private void OnDeleteButtonPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not WpfButton button || FindParent<FrameworkElement>(button) is not { DataContext: MemoItem item })
        {
            return;
        }

        DeleteMemo(item);
    }

    internal void DeleteMemo(MemoItem item)
    {
        if (!_viewModel.Memos.Contains(item))
        {
            return;
        }

        StopDeleteAnimation();
        FrameworkElement? card = FindCard(item);
        List<(MemoItem Memo, double Y)> oldPositions = CaptureFollowingPositions(item);
        DeleteVisual? exit = CreateDeleteVisual(card);
        _viewModel.DeleteItem(item.Id);
        if (exit is not null)
        {
            StartDeleteExitAnimation(exit);
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => StartFlipAnimation(oldPositions)));
        UpdateEditVisual();
    }

    private void OnMemoDeleted(Guid id)
    {
        if (_displayedMemo?.Id != id)
        {
            return;
        }

        MarkdownEditor.AbortForSourceDeletion();
        MemoEditCoordinator.Shared.Release(id, this);
        _viewModel.EndEdit();
        SetDisplayedMemo(null);
        _newMemoDraft = string.Empty;
        MarkdownEditor.BeginNew();
        UpdateEditVisual();
    }

    private List<(MemoItem Memo, double Y)> CaptureFollowingPositions(MemoItem item)
    {
        int index = _viewModel.Memos.IndexOf(item);
        List<(MemoItem Memo, double Y)> positions = [];
        if (index < 0)
        {
            return positions;
        }

        foreach (MemoItem memo in _viewModel.Memos.Skip(index + 1))
        {
            FrameworkElement? card = FindCard(memo);
            if (card is null)
            {
                continue;
            }

            WpfPoint? point = card.TranslatePoint(new WpfPoint(0, 0), MemoList);
            if (point is not null)
            {
                positions.Add((memo, point.Value.Y));
            }
        }

        return positions;
    }

    private FrameworkElement? FindCard(MemoItem item) =>
        FindVisualChildren<FrameworkElement>(MemoList).FirstOrDefault(element => ReferenceEquals(element.DataContext, item) && element.Name == "MemoCard");

    private DeleteVisual? CreateDeleteVisual(FrameworkElement? card)
    {
        if (!MotionPreferences.AnimationsEnabled || card is null || card.ActualWidth < 1 || card.ActualHeight < 1)
        {
            return null;
        }

        WpfPoint position = card.TranslatePoint(new WpfPoint(0, 0), DeleteOverlay);
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(card.ActualWidth * VisualTreeHelper.GetDpi(card).DpiScaleX));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(card.ActualHeight * VisualTreeHelper.GetDpi(card).DpiScaleY));
        RenderTargetBitmap bitmap = new(pixelWidth, pixelHeight, 96 * VisualTreeHelper.GetDpi(card).DpiScaleX, 96 * VisualTreeHelper.GetDpi(card).DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(card);
        ScaleTransform scale = new(1, 1);
        WpfImage image = new()
        {
            Source = bitmap,
            Width = card.ActualWidth,
            Height = card.ActualHeight,
            Opacity = 1,
            IsHitTestVisible = false,
            RenderTransformOrigin = new WpfPoint(0.5, 0.5),
            RenderTransform = scale
        };
        Canvas.SetLeft(image, position.X);
        Canvas.SetTop(image, position.Y);
        DeleteOverlay.Children.Add(image);
        return new DeleteVisual { Layer = DeleteOverlay, Image = image, Bitmap = bitmap, Scale = scale };
    }

    private void StartDeleteExitAnimation(DeleteVisual visual)
    {
        _deleteVisuals.Add(visual);
        FrameAnimation? animation = new();
        visual.Animation = animation;
        animation.Start(TimeSpan.FromMilliseconds(120), MotionEasing.CubicEaseOut, progress =>
        {
            visual.Image.Opacity = 1 - progress;
            visual.Scale.ScaleX = 1 - 0.02 * progress;
            visual.Scale.ScaleY = 1 - 0.02 * progress;
        }, () => DisposeDeleteVisual(visual));
    }

    private void StartFlipAnimation(List<(MemoItem Memo, double Y)> oldPositions)
    {
        if (!MotionPreferences.AnimationsEnabled || oldPositions.Count == 0)
        {
            return;
        }

        foreach ((MemoItem memo, double oldY) in oldPositions)
        {
            FrameworkElement? card = FindCard(memo);
            WpfPoint? current = card?.TranslatePoint(new WpfPoint(0, 0), MemoList);
            if (card is null || current is null)
            {
                continue;
            }

            double inverse = oldY - current.Value.Y;
            if (Math.Abs(inverse) < 0.1)
            {
                continue;
            }

            TranslateTransform transform = new(0, inverse);
            card.RenderTransform = transform;
            _flipEntries.Add(new FlipEntry(card, transform));
        }

        if (_flipEntries.Count == 0)
        {
            return;
        }

        double[] starts = _flipEntries.Select(entry => entry.Transform.Y).ToArray();
        _deleteSlideAnimation = new FrameAnimation();
        _deleteSlideAnimation.Start(TimeSpan.FromMilliseconds(190), MotionEasing.CubicEaseOut, progress =>
        {
            for (int index = 0; index < _flipEntries.Count; index++)
            {
                _flipEntries[index].Transform.Y = starts[index] * (1 - progress);
            }
        }, () =>
        {
            ClearFlipEntries();
            FrameAnimation? animation = _deleteSlideAnimation;
            _deleteSlideAnimation = null;
            animation?.Dispose();
        });
    }

    private void StopDeleteAnimation()
    {
        _deleteGeneration++;
        _deleteSlideAnimation?.Dispose();
        _deleteSlideAnimation = null;
        ClearFlipEntries();
        foreach (DeleteVisual visual in _deleteVisuals.ToArray())
        {
            DisposeDeleteVisual(visual);
        }
    }

    private void ClearFlipEntries()
    {
        foreach (FlipEntry entry in _flipEntries)
        {
            if (ReferenceEquals(entry.Element.RenderTransform, entry.Transform))
            {
                entry.Element.RenderTransform = null;
            }
        }

        _flipEntries.Clear();
    }

    private void DisposeDeleteVisual(DeleteVisual visual)
    {
        visual.Animation?.Dispose();
        visual.Animation = null;
        visual.Layer.Children.Remove(visual.Image);
        visual.Image.Source = null;
        (visual.Bitmap as IDisposable)?.Dispose();
        _deleteVisuals.Remove(visual);
    }

    private void UpdateEditVisual()
    {
        foreach (FrameworkElement card in FindVisualChildren<FrameworkElement>(MemoList).Where(element => element.Name == "MemoCard"))
        {
            bool editing = card.DataContext is MemoItem item && _viewModel.EditingId == item.Id;
            InteractionState.SetIsEditing(card, editing);
            if (FindVisualChildren<Border>(card).FirstOrDefault(border => border.Name == "AccentBar") is { } accent)
            {
                accent.Opacity = editing ? 1 : 0.5;
            }

            if (FindVisualChildren<WpfButton>(card).FirstOrDefault(button => button.Name == "DeleteButton") is { } deleteButton)
            {
                InteractionState.SetIsRevealed(deleteButton, card.IsMouseOver || editing);
            }
        }
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        BeginTitleBarDrag(e);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _hostActions.OpenSettings(_context);

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        TogglePinned();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        _hostActions.MinimizeToTray(_context);
    }

    private async void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_closeRequestInProgress)
        {
            return;
        }

        _closeRequestInProgress = true;
        try
        {
            if (!_settings.HasAskedCloseButtonAction)
            {
                CloseButtonAction? action = await _hostActions.AskCloseButtonAction(_context);
                if (action is null)
                {
                    return;
                }

                _settings.CloseButtonAction = action.Value;
                _settings.HasAskedCloseButtonAction = true;
            }

            if (_settings.CloseButtonAction == CloseButtonAction.Close)
            {
                _hostActions.ExitApplication(_context);
            }
            else
            {
                _hostActions.MinimizeToTray(_context);
            }
        }
        finally
        {
            _closeRequestInProgress = false;
        }
    }

    private void OnWindowPreviewKeyDown(object sender, InputKeyEventArgs e)
    {
        if (e.Key == Key.Escape && !MarkdownEditor.IsNewMemo)
        {
            _ = MarkdownEditor.CancelEditingAsync();
            e.Handled = true;
        }
    }

    private void OnWindowPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_memoOverlayScrollBarThumb is not null
            && InteractionState.GetIsRevealed(_memoOverlayScrollBarThumb))
        {
            ScheduleMemoOverlayScrollBarCollapse();
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        MotionAnimations.Cancel(PinIcon);
        _memoOverlayScrollBarCollapseTimer.Stop();
        _memoOverlayScrollBarCollapseTimer.Tick -= OnMemoOverlayScrollBarCollapseTimerTick;
        _memoOverlayScrollBarTimerLease.Dispose();
        _viewModel.MemoDeleted -= OnMemoDeleted;
        _viewModel.MemosLoaded -= OnMemosLoaded;
        _dragManager?.Dispose();
        _dragManager = null;
        MarkdownEditor.NewRequested -= OnNewRequested;
        if (_displayedMemo is not null)
        {
            _displayedMemo.PropertyChanged -= OnDisplayedMemoChanged;
            MemoEditCoordinator.Shared.Release(_displayedMemo.Id, this);
        }

        StopDeleteAnimation();
        PrepareDockingForClose();
        DisposeDockingInteraction();
        MarkdownEditor.Dispose();
    }

    private static T? FindParent<T>(DependencyObject source) where T : DependencyObject
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is null)
        {
            yield break;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (T descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}

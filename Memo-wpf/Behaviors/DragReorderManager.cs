using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Memo.Models;
using Memo.UI;
using Memo.UI.Animation;
using Memo.UI.Windows;
using Memo.ViewModels;
using CaptureMode = System.Windows.Input.CaptureMode;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventHandler = System.Windows.Input.MouseButtonEventHandler;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseEventHandler = System.Windows.Input.MouseEventHandler;
using Point = System.Windows.Point;
using DropShadowEffect = System.Windows.Media.Effects.DropShadowEffect;
using Popup = System.Windows.Controls.Primitives.Popup;

namespace Memo.Behaviors;

/// <summary>Explicit lifecycle states used by the memo card drag interaction.</summary>
public enum DragReorderState
{
    Idle,
    Pressed,
    Dragging,
    Completing,
    Cancelled
}

internal enum DragInterruptionAction
{
    Ignore,
    Cancel,
    CompleteRelease
}

/// <summary>Placement of the drag preview popup: DIP offsets for the pre-HWND
/// fallback and physical pixel coordinates for SetWindowPos.</summary>
internal readonly record struct DragPopupPlacement(double OffsetXDip, double OffsetYDip, int PixelLeft, int PixelTop);

/// <summary>
/// Owns memo card drag/reorder interaction.
///
/// The collection is deliberately untouched while the pointer is moving. The
/// dragged card keeps its layout slot but is hidden, while a translucent
/// snapshot follows the pointer in an independent popup HWND. Sibling cards
/// use only RenderTransform to preview the insertion point. A valid in-window
/// release commits one call to <see cref="MainViewModel.MoveItem"/>.
/// </summary>
public sealed class DragReorderManager : IDisposable
{
    public const double DragThreshold = 8;
    public const double EdgeThreshold = 40;
    public const double MaxScrollSpeedPerSecond = 750;
    public const double PlaceholderOpacity = 0.35;
    public const double ShadowPad = 18;
    public const double SlideDurationMilliseconds = 180;

    private readonly ItemsControl _items;
    private readonly ScrollViewer _scroller;
    private readonly MainViewModel _viewModel;
    private readonly Action<MemoItem, Point>? _requestPopout;
    private readonly IMonitorService _monitorService;
    private Window? _window;

    private readonly List<FrameworkElement> _cards = [];
    private readonly Dictionary<FrameworkElement, double> _layoutY = [];
    private readonly Dictionary<FrameworkElement, Transform?> _originalTransforms = [];
    private readonly Dictionary<FrameworkElement, TranslateTransform> _slideTransforms = [];
    private readonly Dictionary<FrameworkElement, FrameAnimation> _slideAnimations = [];

    private MemoItem? _draggedItem;
    private FrameworkElement? _draggedCard;
    private int _startIndex = -1;
    private int _insertIndex = -1;
    private Point _downPoint;
    private Point _latestItemsPoint;
    private Point _cursorOffset;
    private CaptureMode _captureMode = CaptureMode.SubTree;
    private FrameworkElement? _capturedElement;

    private Popup? _floatingPopup;
    private nint _floatingPopupHandle;
    private Border? _floatingOuter;
    private Image? _floatingImage;
    private BitmapSource? _floatingBitmap;
    private (int Left, int Top)? _lastAppliedPopupPixelPosition;
    private bool _renderingSubscribed;
    private bool _inputTrackingSubscribed;
    private TimeSpan? _lastRenderingTime;
    private Point? _lastScreenPoint;
    private bool _attached;
    private bool _disposed;
    private bool _commitIssued;

    private static readonly nint HwndTop = nint.Zero;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpShowWindow = 0x0040;

    public DragReorderManager(
        ItemsControl items,
        ScrollViewer scroller,
        Canvas layer,
        MainViewModel viewModel,
        Action<MemoItem, Point>? requestPopout = null,
        IMonitorService? monitorService = null)
        : this(items, scroller, viewModel, requestPopout, monitorService)
    {
        ArgumentNullException.ThrowIfNull(layer);
    }

    public DragReorderManager(
        ItemsControl items,
        ScrollViewer scroller,
        MainViewModel viewModel,
        Action<MemoItem, Point>? requestPopout = null,
        IMonitorService? monitorService = null)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _scroller = scroller ?? throw new ArgumentNullException(nameof(scroller));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _requestPopout = requestPopout;
        _monitorService = monitorService ?? new MonitorService();
        _window = Window.GetWindow(items);
        State = DragReorderState.Idle;
    }

    public DragReorderState State { get; private set; }
    public bool IsDragging => State == DragReorderState.Dragging;
    public bool IsDragPopupOpen => _floatingPopup?.IsOpen == true;
    public MemoItem? DraggedItem => _draggedItem;
    public int StartIndex => _startIndex;
    public int InsertIndex => _insertIndex;

    internal static int ResolveInsertIndex(int rawIndex, int startIndex, int itemCount)
    {
        if (itemCount <= 0)
        {
            return -1;
        }

        int target = rawIndex;
        if (target > startIndex)
        {
            target--;
        }

        return Math.Clamp(target, 0, itemCount - 1);
    }

    internal static DragInterruptionAction ResolveInterruption(
        DragReorderState state,
        bool leftButtonPressed) =>
        state switch
        {
            DragReorderState.Pressed => DragInterruptionAction.Cancel,
            DragReorderState.Dragging when leftButtonPressed => DragInterruptionAction.Cancel,
            DragReorderState.Dragging => DragInterruptionAction.CompleteRelease,
            _ => DragInterruptionAction.Ignore
        };

    /// <summary>Attach input, collection and window lifecycle handlers once.</summary>
    public void Attach()
    {
        if (_attached || _disposed)
        {
            return;
        }

        _attached = true;
        _items.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPreviewMouseLeftButtonDown), true);
        _items.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnPreviewMouseMove), true);
        _items.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnPreviewMouseLeftButtonUp), true);
        _items.AddHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler(OnLostMouseCapture), true);
        _viewModel.Memos.CollectionChanged += OnMemosCollectionChanged;
        Window? window = _window ??= Window.GetWindow(_items);
        if (window is not null)
        {
            window.PreviewKeyDown += OnWindowPreviewKeyDown;
            window.Deactivated += OnWindowDeactivated;
            window.Closed += OnWindowClosed;
        }
    }

    public void Detach()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        _items.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPreviewMouseLeftButtonDown));
        _items.RemoveHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnPreviewMouseMove));
        _items.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnPreviewMouseLeftButtonUp));
        _items.RemoveHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler(OnLostMouseCapture));
        _viewModel.Memos.CollectionChanged -= OnMemosCollectionChanged;
        if (_window is not null)
        {
            _window.PreviewKeyDown -= OnWindowPreviewKeyDown;
            _window.Deactivated -= OnWindowDeactivated;
            _window.Closed -= OnWindowClosed;
        }

        CancelDrag();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Detach();
        _disposed = true;
        StopRenderingLoop();
        RemoveDragVisuals();
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_disposed || State is DragReorderState.Pressed or DragReorderState.Dragging or DragReorderState.Completing)
        {
            return;
        }

        if (e.ChangedButton != MouseButton.Left || IsDeleteButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        MemoItem? item = FindMemoItem(e.OriginalSource as DependencyObject);
        FrameworkElement? card = item is null ? null : FindCard(item);
        int index = item is null ? -1 : _viewModel.Memos.IndexOf(item);
        if (item is null || card is null || index < 0)
        {
            return;
        }

        _draggedItem = item;
        _draggedCard = card;
        _startIndex = index;
        _insertIndex = index;
        _downPoint = e.GetPosition(_items);
        _latestItemsPoint = _downPoint;
        _lastScreenPoint = TryGetScreenPoint(e);
        _commitIssued = false;
        State = DragReorderState.Pressed;
        CaptureMouse();
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedItem is null || State is DragReorderState.Idle or DragReorderState.Cancelled or DragReorderState.Completing)
        {
            return;
        }

        Point point = e.GetPosition(_items);
        _latestItemsPoint = point;
        Point? screenPoint = TryGetScreenPoint(e);
        if (screenPoint.HasValue)
        {
            _lastScreenPoint = screenPoint;
        }
        if (State == DragReorderState.Pressed)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                CancelDrag();
                return;
            }

            if (Math.Abs(point.X - _downPoint.X) <= DragThreshold && Math.Abs(point.Y - _downPoint.Y) <= DragThreshold)
            {
                return;
            }

            BeginDrag();
            if (!IsDragging)
            {
                return;
            }
        }

        UpdateFloatingPosition(point);
        UpdateInsertion(point.Y);
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggedItem is null)
        {
            return;
        }

        if (State == DragReorderState.Pressed)
        {
            CancelDrag();
            return;
        }

        if (State != DragReorderState.Dragging)
        {
            return;
        }

        Point? screenPoint = TryGetScreenPoint(e) ?? _lastScreenPoint;
        bool outside = IsOutsideWindow(screenPoint);
        CompleteDrag(screenPoint, outside);
        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        HandleInterruption();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && State is DragReorderState.Pressed or DragReorderState.Dragging)
        {
            CancelDrag();
            e.Handled = true;
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        HandleInterruption();
    }

    private void HandleInterruption()
    {
        if (State is not (DragReorderState.Pressed or DragReorderState.Dragging))
        {
            return;
        }

        bool hasCursorState = TryGetCursorState(out Point screenPoint, out bool leftButtonPressed);
        if (hasCursorState)
        {
            _lastScreenPoint = screenPoint;
        }

        switch (ResolveInterruption(State, leftButtonPressed))
        {
            case DragInterruptionAction.Cancel:
                CancelDrag();
                break;
            case DragInterruptionAction.CompleteRelease:
                CompleteOutsideOrCancelFromLastScreenPoint();
                break;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (State is DragReorderState.Pressed or DragReorderState.Dragging)
        {
            CancelDrag();
        }
    }

    private void OnMemosCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_draggedItem is not null && State is (DragReorderState.Pressed or DragReorderState.Dragging))
        {
            CancelDrag();
        }
    }

    private void BeginDrag()
    {
        if (_draggedItem is null || _draggedCard is null || _viewModel.Memos.IndexOf(_draggedItem) < 0)
        {
            CancelDrag();
            return;
        }

        State = DragReorderState.Dragging;
        _cards.Clear();
        _cards.AddRange(GetCardsInOrder());
        // A preview can only be committed when the visual order is a complete
        // representation of the collection. This is normally guaranteed by
        // the non-virtualized panel, but a disconnected container must fail
        // closed instead of allowing a stale index to reach MoveItem.
        if (_cards.Count != _viewModel.Memos.Count ||
            !_cards.Contains(_draggedCard) ||
            _cards.IndexOf(_draggedCard) != _startIndex)
        {
            CancelDrag();
            return;
        }

        _layoutY.Clear();
        _originalTransforms.Clear();
        foreach (FrameworkElement card in _cards)
        {
            Point? point;
            try
            {
                point = card.TranslatePoint(new Point(0, 0), _items);
            }
            catch (InvalidOperationException)
            {
                CancelDrag();
                return;
            }

            if (point is null)
            {
                CancelDrag();
                return;
            }

            _layoutY[card] = point.Value.Y;
            _originalTransforms[card] = card.RenderTransform;
        }

        Point? cardOrigin = _draggedCard.TranslatePoint(new Point(0, 0), _items);
        _cursorOffset = cardOrigin is null ? new Point(0, 0) : new Point(_downPoint.X - cardOrigin.Value.X, _downPoint.Y - cardOrigin.Value.Y);
        CaptureMouse();
        CreateDragVisuals();
        if (_floatingOuter is null)
        {
            CancelDrag();
            return;
        }

        // Capture the full-fidelity card first, then keep the original layout
        // slot for stable insertion calculations without leaving a translucent
        // copy behind at the source position.
        _draggedCard.Opacity = 0;
        BeginInputTracking();
        BeginRenderingLoop();
        if (_lastScreenPoint is Point screenPoint)
        {
            UpdateFloatingPositionOnScreen(screenPoint);
        }
        UpdateInsertion(_latestItemsPoint.Y);
    }

    private void CompleteDrag(Point? screenPoint, bool outside)
    {
        if (State != DragReorderState.Dragging || _commitIssued)
        {
            return;
        }

        State = DragReorderState.Completing;
        _commitIssued = true;
        MemoItem? item = _draggedItem;
        int start = _startIndex;
        int target = _insertIndex;
        CleanupDragState();

        try
        {
            if (item is null || !_viewModel.Memos.Contains(item))
            {
                return;
            }

            if (outside && screenPoint.HasValue && _requestPopout is not null)
            {
                _requestPopout(item, screenPoint.Value);
                return;
            }

            if (!outside && target >= 0 && target < _viewModel.Memos.Count && target != start)
            {
                _viewModel.MoveItem(item.Id, target);
            }
        }
        finally
        {
            ResetToIdle();
        }
    }

    private void CompleteFromLastScreenPoint()
    {
        if (State != DragReorderState.Dragging)
        {
            return;
        }

        Point? screenPoint = TryGetCurrentScreenPoint() ?? _lastScreenPoint;
        if (screenPoint.HasValue)
        {
            _lastScreenPoint = screenPoint;
        }

        CompleteDrag(screenPoint, IsOutsideWindow(screenPoint));
    }

    private void CompleteOutsideOrCancelFromLastScreenPoint()
    {
        if (State != DragReorderState.Dragging)
        {
            return;
        }

        Point? screenPoint = TryGetCurrentScreenPoint() ?? _lastScreenPoint;
        if (screenPoint.HasValue)
        {
            _lastScreenPoint = screenPoint;
        }

        if (screenPoint.HasValue && IsOutsideWindow(screenPoint))
        {
            CompleteDrag(screenPoint, outside: true);
        }
        else
        {
            CancelDrag();
        }
    }

    private void CancelDrag()
    {
        if (State == DragReorderState.Idle || State == DragReorderState.Cancelled)
        {
            return;
        }

        State = DragReorderState.Cancelled;
        CleanupDragState();
        _draggedItem = null;
        _draggedCard = null;
        _startIndex = -1;
        _insertIndex = -1;
        _downPoint = default;
        _latestItemsPoint = default;
        _lastScreenPoint = null;
        _commitIssued = false;
    }

    private void ResetToIdle()
    {
        _draggedItem = null;
        _draggedCard = null;
        _startIndex = -1;
        _insertIndex = -1;
        _downPoint = default;
        _latestItemsPoint = default;
        _lastScreenPoint = null;
        _commitIssued = false;
        State = DragReorderState.Idle;
    }

    private void CleanupDragState()
    {
        StopInputTracking();
        StopRenderingLoop();
        ReleaseMouse();
        RemoveDragVisuals();
        if (_draggedCard is not null)
        {
            _draggedCard.Opacity = 1;
        }

        _cards.Clear();
        _layoutY.Clear();
        _originalTransforms.Clear();
        _startIndex = _startIndex < 0 ? -1 : _startIndex;
    }

    private void CaptureMouse()
    {
        _capturedElement = _items;
        Mouse.Capture(_items, _captureMode);
    }

    private void ReleaseMouse()
    {
        if (_capturedElement is not null && ReferenceEquals(Mouse.Captured, _capturedElement))
        {
            Mouse.Capture(null);
        }

        _capturedElement = null;
    }

    private void CreateDragVisuals()
    {
        if (_draggedCard is null || _draggedCard.ActualWidth < 1 || _draggedCard.ActualHeight < 1)
        {
            return;
        }

        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(_draggedCard);
            int pixelWidth = Math.Max(1, (int)Math.Ceiling(_draggedCard.ActualWidth * dpi.DpiScaleX));
            int pixelHeight = Math.Max(1, (int)Math.Ceiling(_draggedCard.ActualHeight * dpi.DpiScaleY));
            RenderTargetBitmap bitmap = new(pixelWidth, pixelHeight, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(_draggedCard);
            _floatingBitmap = bitmap;
            _floatingImage = new Image
            {
                Source = bitmap,
                Width = _draggedCard.ActualWidth,
                Height = _draggedCard.ActualHeight,
                Stretch = Stretch.Fill,
                Opacity = 1 - PlaceholderOpacity,
                IsHitTestVisible = false
            };
            _floatingOuter = new Border
            {
                Width = _draggedCard.ActualWidth + (ShadowPad * 2),
                Height = _draggedCard.ActualHeight + (ShadowPad * 2),
                Padding = new Thickness(ShadowPad),
                Background = System.Windows.Media.Brushes.Transparent,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 20,
                    ShadowDepth = 8,
                    Opacity = 0.24,
                    Direction = 270
                },
                Child = _floatingImage,
                IsHitTestVisible = false
            };
            _floatingPopup = new Popup
            {
                AllowsTransparency = true,
                Focusable = false,
                IsHitTestVisible = false,
                Placement = PlacementMode.AbsolutePoint,
                PlacementTarget = _window,
                PopupAnimation = PopupAnimation.None,
                StaysOpen = true,
                Child = _floatingOuter
            };
            _floatingPopup.Opened += OnFloatingPopupOpened;
            SeedFallbackPopupOffset();
            _floatingPopup.IsOpen = true;
            TryAttachPopupHandle();
        }
        catch (InvalidOperationException)
        {
            if (_floatingPopup is not null)
            {
                _floatingPopup.Opened -= OnFloatingPopupOpened;
                _floatingPopup.IsOpen = false;
                _floatingPopup.Child = null;
            }

            _floatingBitmap = null;
            _floatingImage = null;
            _floatingOuter = null;
            _floatingPopup = null;
            _floatingPopupHandle = nint.Zero;
        }

    }

    private void RemoveDragVisuals()
    {
        foreach (FrameAnimation animation in _slideAnimations.Values)
        {
            animation.Dispose();
        }

        _slideAnimations.Clear();
        foreach (FrameworkElement card in _slideTransforms.Keys.ToArray())
        {
            if (_originalTransforms.TryGetValue(card, out Transform? original))
            {
                card.RenderTransform = original;
            }
            else
            {
                card.RenderTransform = null;
            }
        }

        _slideTransforms.Clear();
        if (_floatingPopup is not null)
        {
            _floatingPopup.Opened -= OnFloatingPopupOpened;
            _floatingPopup.IsOpen = false;
            _floatingPopup.Child = null;
        }

        _floatingPopup = null;
        _floatingPopupHandle = nint.Zero;
        _lastAppliedPopupPixelPosition = null;
        _floatingOuter = null;
        _floatingImage = null;
        _floatingBitmap = null;
    }

    private void UpdateFloatingPosition(Point itemsPoint)
    {
        Point? screenPoint = TryGetScreenPointFromItems(itemsPoint);
        if (screenPoint is Point point)
        {
            UpdateFloatingPositionOnScreen(point);
        }
    }

    private void UpdateFloatingPositionOnScreen(Point screenPoint)
    {
        if (_floatingPopup is null || _floatingOuter is null)
        {
            return;
        }

        TryAttachPopupHandle();
        DragPopupPlacement placement = CalculatePopupPlacement(screenPoint, _cursorOffset, _monitorService.FromPoint(screenPoint).Dpi);

        // While the popup HWND is still unavailable, the DIP offsets are the
        // only placement mechanism. Once the handle exists, SetWindowPos must
        // be the single mover: offset changes make WPF reposition the popup
        // itself, and WPF nudges a popup back inside the monitor when it
        // crosses a screen edge, so the two mechanisms fight and flicker the
        // preview at the edge.
        if (_floatingPopupHandle == nint.Zero)
        {
            _floatingPopup.HorizontalOffset = placement.OffsetXDip;
            _floatingPopup.VerticalOffset = placement.OffsetYDip;
            return;
        }

        if (_lastAppliedPopupPixelPosition == (placement.PixelLeft, placement.PixelTop))
        {
            return;
        }

        _lastAppliedPopupPixelPosition = (placement.PixelLeft, placement.PixelTop);
        SetWindowPos(
            _floatingPopupHandle,
            HwndTop,
            placement.PixelLeft,
            placement.PixelTop,
            0,
            0,
            SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | SwpShowWindow);
    }

    internal static Point CalculatePreviewTopLeftPixels(
        Point screenPointPixels,
        Point cursorOffsetDip,
        DpiScale2 dpi)
    {
        Point cursorOffsetPixels = dpi.DipToPixels(cursorOffsetDip);
        return new Point(
            screenPointPixels.X - cursorOffsetPixels.X - (ShadowPad * dpi.ScaleX),
            screenPointPixels.Y - cursorOffsetPixels.Y - (ShadowPad * dpi.ScaleY));
    }

    internal static DragPopupPlacement CalculatePopupPlacement(
        Point screenPointPixels,
        Point cursorOffsetDip,
        DpiScale2 dpi)
    {
        Point popupTopLeftPixels = CalculatePreviewTopLeftPixels(screenPointPixels, cursorOffsetDip, dpi);
        Point popupTopLeftDip = dpi.PixelsToDip(popupTopLeftPixels);
        return new DragPopupPlacement(
            popupTopLeftDip.X,
            popupTopLeftDip.Y,
            (int)Math.Round(popupTopLeftPixels.X),
            (int)Math.Round(popupTopLeftPixels.Y));
    }

    // The popup opens before its HWND can be located; seed the DIP offsets so
    // the preview appears at the cursor instead of the screen origin.
    private void SeedFallbackPopupOffset()
    {
        if (_floatingPopup is null || _lastScreenPoint is not Point screenPoint)
        {
            return;
        }

        DragPopupPlacement placement = CalculatePopupPlacement(screenPoint, _cursorOffset, _monitorService.FromPoint(screenPoint).Dpi);
        _floatingPopup.HorizontalOffset = placement.OffsetXDip;
        _floatingPopup.VerticalOffset = placement.OffsetYDip;
    }

    private void OnFloatingPopupOpened(object? sender, EventArgs e)
    {
        TryAttachPopupHandle();
        // The open sequence may have nudged the popup back on-screen from the
        // seeded offsets; force the next request to reposition from pixels.
        _lastAppliedPopupPixelPosition = null;
        if (_lastScreenPoint is Point screenPoint)
        {
            UpdateFloatingPositionOnScreen(screenPoint);
        }
    }

    private void TryAttachPopupHandle()
    {
        if (_floatingPopupHandle != nint.Zero || _floatingOuter is null)
        {
            return;
        }

        if (PresentationSource.FromVisual(_floatingOuter) is HwndSource source)
        {
            _floatingPopupHandle = source.Handle;
        }
    }

    private Point? TryGetScreenPointFromItems(Point itemsPoint)
    {
        if (_window is null || !_window.IsLoaded)
        {
            return null;
        }

        try
        {
            Point? windowPoint = _items.TranslatePoint(itemsPoint, _window);
            return windowPoint.HasValue ? _monitorService.ToScreenPixels(_window, windowPoint.Value) : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void UpdateInsertion(double pointerY)
    {
        if (!IsDragging || _cards.Count == 0)
        {
            return;
        }

        int target = 0;
        for (int index = 0; index < _cards.Count; index++)
        {
            FrameworkElement card = _cards[index];
            if (ReferenceEquals(card, _draggedCard) || !_layoutY.TryGetValue(card, out double y))
            {
                continue;
            }

            double center = y + (card.ActualHeight / 2);
            if (pointerY > center)
            {
                target = index + 1;
            }
        }

        // The scan uses the original visual order, which still contains the
        // dragged card; resolve that slot against the final collection order.
        target = ResolveInsertIndex(target, _startIndex, _cards.Count);
        if (target == _insertIndex)
        {
            return;
        }

        _insertIndex = target;
        BeginNeighborSlides();
    }

    private void BeginNeighborSlides()
    {
        if (_draggedCard is null || _startIndex < 0 || _insertIndex < 0)
        {
            return;
        }

        foreach (FrameworkElement card in _cards)
        {
            if (ReferenceEquals(card, _draggedCard) || !_layoutY.ContainsKey(card))
            {
                continue;
            }

            double destination = 0;
            int index = _cards.IndexOf(card);
            if (_insertIndex > _startIndex && index > _startIndex && index <= _insertIndex)
            {
                destination = GetLayoutY(index - 1) - GetLayoutY(index);
            }
            else if (_insertIndex < _startIndex && index >= _insertIndex && index < _startIndex)
            {
                destination = GetLayoutY(index + 1) - GetLayoutY(index);
            }

            AnimateSlide(card, destination);
        }
    }

    private double GetLayoutY(int index) => index >= 0 && index < _cards.Count && _layoutY.TryGetValue(_cards[index], out double y) ? y : 0;

    private void AnimateSlide(FrameworkElement card, double destination)
    {
        if (!_slideTransforms.TryGetValue(card, out TranslateTransform? transform))
        {
            transform = new TranslateTransform();
            _slideTransforms[card] = transform;
            card.RenderTransform = transform;
        }

        _slideAnimations.Remove(card, out FrameAnimation? previous);
        previous?.Dispose();
        double from = transform.Y;
        if (!MotionPreferences.AnimationsEnabled)
        {
            transform.Y = destination;
            return;
        }

        FrameAnimation? animation = null;
        animation = new FrameAnimation();
        _slideAnimations[card] = animation;
        animation.Start(TimeSpan.FromMilliseconds(SlideDurationMilliseconds), MotionEasing.CubicEaseOut, progress =>
        {
            if (ReferenceEquals(card.RenderTransform, transform))
            {
                transform.Y = from + ((destination - from) * progress);
            }
        }, () =>
        {
            transform.Y = destination;
            if (_slideAnimations.TryGetValue(card, out FrameAnimation? current) && ReferenceEquals(current, animation))
            {
                _slideAnimations.Remove(card);
                animation.Dispose();
            }
        });
    }

    private void BeginRenderingLoop()
    {
        if (_renderingSubscribed)
        {
            return;
        }

        _renderingSubscribed = true;
        _lastRenderingTime = null;
        CompositionTarget.Rendering += OnRendering;
    }

    private void BeginInputTracking()
    {
        if (_inputTrackingSubscribed)
        {
            return;
        }

        _inputTrackingSubscribed = true;
        InputManager.Current.PreProcessInput += OnPreProcessInput;
    }

    private void StopInputTracking()
    {
        if (!_inputTrackingSubscribed)
        {
            return;
        }

        InputManager.Current.PreProcessInput -= OnPreProcessInput;
        _inputTrackingSubscribed = false;
    }

    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (State is not (DragReorderState.Pressed or DragReorderState.Dragging))
        {
            return;
        }

        if (e.StagingItem.Input is KeyEventArgs { Key: Key.Escape, IsDown: true } key)
        {
            CancelDrag();
            key.Handled = true;
            return;
        }

        if (State != DragReorderState.Dragging ||
            e.StagingItem.Input is not MouseButtonEventArgs { ChangedButton: MouseButton.Left, ButtonState: MouseButtonState.Released } mouse)
        {
            return;
        }

        CompleteFromLastScreenPoint();
        mouse.Handled = true;
    }

    private void StopRenderingLoop()
    {
        if (!_renderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _renderingSubscribed = false;
        _lastRenderingTime = null;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsDragging)
        {
            return;
        }

        if (TryGetCursorState(out Point screenPoint, out bool leftButtonPressed))
        {
            _lastScreenPoint = screenPoint;
            if (!leftButtonPressed)
            {
                CompleteDrag(screenPoint, IsOutsideWindow(screenPoint));
                return;
            }
        }

        TimeSpan timestamp = e is RenderingEventArgs rendering ? rendering.RenderingTime : TimeSpan.Zero;
        double elapsedSeconds = _lastRenderingTime.HasValue
            ? Math.Clamp((timestamp - _lastRenderingTime.Value).TotalSeconds, 0, 0.05)
            : 0;
        _lastRenderingTime = timestamp;
        if (elapsedSeconds <= 0)
        {
            return;
        }

        ApplyEdgeScroll(elapsedSeconds);
        if (_floatingPopup is not null)
        {
            if (_lastScreenPoint is Point latestScreenPoint)
            {
                UpdateFloatingPositionOnScreen(latestScreenPoint);
            }
            else
            {
                UpdateFloatingPosition(Mouse.GetPosition(_items));
            }
        }
    }

    private void ApplyEdgeScroll(double elapsedSeconds)
    {
        if (_scroller.ActualHeight <= 0 || _scroller.ScrollableHeight <= 0)
        {
            return;
        }

        Point pointer = Mouse.GetPosition(_scroller);
        int direction;
        double distance;
        if (pointer.Y <= EdgeThreshold)
        {
            direction = -1;
            distance = pointer.Y;
        }
        else if (pointer.Y >= _scroller.ActualHeight - EdgeThreshold)
        {
            direction = 1;
            distance = _scroller.ActualHeight - pointer.Y;
        }
        else
        {
            return;
        }

        double strength = Math.Clamp((EdgeThreshold - Math.Max(0, distance)) / EdgeThreshold, 0, 1);
        double delta = direction * MaxScrollSpeedPerSecond * strength * elapsedSeconds;
        double oldOffset = _scroller.VerticalOffset;
        double nextOffset = Math.Clamp(oldOffset + delta, 0, _scroller.ScrollableHeight);
        if (Math.Abs(nextOffset - oldOffset) < 0.01)
        {
            return;
        }

        _scroller.ScrollToVerticalOffset(nextOffset);
        Point next = Mouse.GetPosition(_items);
        _latestItemsPoint = next;
        UpdateInsertion(next.Y);
    }

    private Point? TryGetScreenPoint(MouseEventArgs e)
    {
        if (_window is null || !_window.IsLoaded)
        {
            return null;
        }

        try
        {
            Point windowPoint = e.GetPosition(_window);
            return _monitorService.ToScreenPixels(_window, windowPoint);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private Point? TryGetCurrentScreenPoint()
    {
        if (TryGetCursorState(out Point screenPoint, out _))
        {
            return screenPoint;
        }

        if (_window is null || !_window.IsLoaded)
        {
            return null;
        }

        try
        {
            return _monitorService.ToScreenPixels(_window, Mouse.GetPosition(_window));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private bool TryGetCursorState(out Point screenPoint, out bool leftButtonPressed)
    {
        if (_monitorService is ICursorStateService cursorService
            && cursorService.TryGetCursorState(out screenPoint, out leftButtonPressed))
        {
            return true;
        }

        screenPoint = default;
        leftButtonPressed = Mouse.LeftButton == MouseButtonState.Pressed;
        return false;
    }

    private bool IsOutsideWindow(Point? screenPoint)
    {
        if (!screenPoint.HasValue || _window is null || !_window.IsLoaded)
        {
            return false;
        }

        try
        {
            Point topLeft = _monitorService.ToScreenPixels(_window, new Point(0, 0));
            Point bottomRight = _monitorService.ToScreenPixels(_window, new Point(_window.ActualWidth, _window.ActualHeight));
            Rect bounds = new(topLeft, bottomRight);
            return !bounds.Contains(screenPoint.Value);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private List<FrameworkElement> GetCardsInOrder()
    {
        List<(FrameworkElement Card, double Y)> cards = [];
        foreach (FrameworkElement element in FindVisualDescendants<FrameworkElement>(_items)
            .Where(element => element.Name == "MemoCard" && element.DataContext is MemoItem))
        {
            try
            {
                Point? point = element.TranslatePoint(new Point(0, 0), _items);
                if (point.HasValue)
                {
                    cards.Add((element, point.Value.Y));
                }
            }
            catch (InvalidOperationException)
            {
                // A virtualized/disconnected container can disappear between
                // visual-tree enumeration and coordinate conversion.
            }
        }

        return cards.OrderBy(entry => entry.Y).Select(entry => entry.Card).ToList();
    }

    private FrameworkElement? FindCard(MemoItem item) =>
        GetCardsInOrder().FirstOrDefault(card => ReferenceEquals(card.DataContext, item));

    private static MemoItem? FindMemoItem(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: MemoItem item })
            {
                return item;
            }
        }

        return null;
    }

    private static bool IsDeleteButton(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement element && element.Name == "DeleteButton")
            {
                return true;
            }
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (T descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Rendering;
using MemeMomo.Markdown;
using MemeMomo.UI;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace MemeMomo.Editor;

/// <summary>
/// Inline image element for the WYSIWYG editor. Shows the bitmap at its natural pixel
/// size (one device-independent unit per pixel) and only scales down — keeping the
/// aspect ratio — when the image is wider than the editor's text line. A ",NN%" suffix
/// in the Markdown source ("![名称](链接),25%") instead pins the width to that percentage
/// of the editor's text width, always capped to the line itself.
/// <para>
/// Hovering the control opens a mini toolbar floating over the image's bottom-left corner
/// with the width options 自适应/25%/50%/75%/100%. The toolbar stays glued to the image's
/// bottom edge: while that edge is scrolled out below the viewport the toolbar rests on
/// the editor's visible bottom edge, scrolling lifts it together with the image's bottom
/// edge, and once no part of the image is visible the toolbar closes. Choosing one option
/// rewrites the image's source text (adding, replacing or removing the ",NN%" suffix)
/// through the adapter, so the visible size and the source syntax stay the same fact.
/// </para>
/// <para>
/// AvalonEdit rebuilds the hosting visual line whenever an inline object's DesiredSize
/// changes. Applying the bitmap (and its larger size) after the control entered the
/// tree therefore makes every rebuild start from the small placeholder and grow again:
/// an endless rebuild → load → apply → rebuild loop that permanently shows the
/// placeholder. Construction instead applies an already-completed loader result
/// synchronously, so a rebuild always produces the final-size control; only the
/// genuinely first load goes through the deferred async path, and it settles after the
/// single rebuild it causes.
/// </para>
/// </summary>
internal sealed class MarkdownImageControl : Border
{
    private const double PlaceholderWidth = 132;
    private const double PlaceholderHeight = 72;
    private const double ImageMargin = 2;
    private const double SpinnerSize = 16;
    private const double SpinnerStrokeThickness = 2;
    private const double SpinnerSweepDegrees = 285;
    private const double SpinnerTextGap = 6;
    private const double SpinnerRotationMilliseconds = 900;
    private const double ToolbarCloseDelayMilliseconds = 300;
    private const double ToolbarEdgeGap = 2;
    private const double ToolbarAnimationMilliseconds = 120;
    private const double ToolbarSlideOffset = 4;
    private const double ToolbarRepositionNudge = 0.01;
    private static readonly double[] ToolbarPercentOptions = [25, 50, 75, 100];

    private readonly MarkdownImageLoader _loader;
    private readonly TextView _textView;
    private readonly AvalonEditMarkdownAdapter _adapter;
    private readonly MarkdownVisualSpan _span;
    private readonly DispatcherTimer _toolbarCloseTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(ToolbarCloseDelayMilliseconds)
    };
    private readonly List<Border> _toolbarOptions = [];
    private Popup? _toolbar;
    private Border? _toolbarSurface;
    private TranslateTransform? _toolbarTranslate;
    private bool _toolbarClosing;
    private bool _toolbarNudgeFlip;
    private Point _lastToolbarAnchor;
    private Size _lastToolbarViewport;
    private Point _lastToolbarPlacement;
    private BitmapSource? _bitmap;
    private Image? _image;
    private Shape? _spinner;
    private CancellationTokenSource? _cancellation;
    private bool _loading;
    private int _loadGeneration;
    private DispatcherOperation? _sizeImageOperation;
    private bool _sizeImageQueued;

    internal MarkdownImageControl(
        MarkdownImageLoader loader,
        TextView textView,
        MarkdownVisualSpan span,
        AvalonEditMarkdownAdapter adapter)
    {
        _loader = loader;
        _textView = textView;
        _span = span;
        _adapter = adapter;
        ImageUri = span.ImageUri ?? string.Empty;
        Margin = new Thickness(2);
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1);
        BorderBrush = Application.Current?.TryFindResource("BorderDefaultBrush") as Brush
            ?? Brushes.LightGray;
        Background = Application.Current?.TryFindResource("BgTertiaryBrush") as Brush
            ?? Brushes.WhiteSmoke;
        MemeMomo.UI.Text.LocalizeExtension.Set(this, AutomationProperties.NameProperty, "图片：{0}", span.AltText ?? string.Empty);
        _toolbarCloseTimer.Tick += (_, _) => CloseToolbar();
        MouseEnter += OnImageMouseEnter;
        MouseLeave += OnImageMouseLeave;
        LayoutUpdated += OnControlLayoutUpdated;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        if (_loader.TryGetCompleted(ImageUri, out MarkdownImageLoadResult? completed) &&
            completed?.Bitmap is { } ready)
        {
            // Cached rebuild path: apply the final bitmap right away so the rebuild
            // starts at its settled size. OnLoaded still runs for this control and its
            // bitmap guard below skips the async load — it must run, because only it
            // subscribes the TextView size watcher that rescales the image when the
            // scrollbar appears or the editor is resized.
            ApplyBitmap(ready);
            return;
        }

        MinWidth = PlaceholderWidth;
        MinHeight = PlaceholderHeight;
        Child = BuildLoadingPlaceholder(span.AltText);
    }

    internal string ImageUri { get; }

    /// <summary>
    /// Builds the placeholder shown until the bitmap arrives: a spinning arc over the
    /// alt text. The arc rotates only while in the visual tree — swapping the child in
    /// <see cref="ApplyBitmap"/> unloads it and thereby stops its animation clock.
    /// </summary>
    private FrameworkElement BuildLoadingPlaceholder(string? altText)
    {
        TextBlock text = new()
        {
            Text = string.IsNullOrWhiteSpace(altText) ? "图片" : altText,
            Margin = new Thickness(8, 0, 8, 8),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        StackPanel panel = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (string.IsNullOrWhiteSpace(altText))
            MemeMomo.UI.Text.LocalizeExtension.Set(text, TextBlock.TextProperty, "图片");
        panel.Children.Add(CreateSpinner());
        panel.Children.Add(text);
        return panel;
    }

    private Shape CreateSpinner()
    {
        double half = SpinnerSize / 2;
        double radius = half - (SpinnerStrokeThickness / 2);
        double sweepRadians = SpinnerSweepDegrees * Math.PI / 180;
        PathFigure figure = new()
        {
            // Start at 12 o'clock and sweep clockwise, leaving the gap where a
            // classic indeterminate spinner keeps its tail.
            StartPoint = new Point(half, half - radius),
            Segments =
            {
                new ArcSegment
                {
                    Point = new Point(
                        half + (radius * Math.Sin(sweepRadians)),
                        half - (radius * Math.Cos(sweepRadians))),
                    Size = new Size(radius, radius),
                    IsLargeArc = SpinnerSweepDegrees > 180,
                    SweepDirection = SweepDirection.Clockwise
                }
            }
        };
        Path spinner = new()
        {
            Width = SpinnerSize,
            Height = SpinnerSize,
            Data = new PathGeometry([figure]),
            StrokeThickness = SpinnerStrokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = new RotateTransform(0, half, half),
            Margin = new Thickness(0, 0, 0, SpinnerTextGap),
            IsHitTestVisible = false
        };
        spinner.SetResourceReference(Shape.StrokeProperty, "AccentPrimaryBrush");
        spinner.Loaded += OnSpinnerLoaded;
        spinner.Unloaded += OnSpinnerUnloaded;
        _spinner = spinner;
        return spinner;
    }

    private void OnSpinnerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Path { RenderTransform: RotateTransform rotation } &&
            MotionPreferences.AnimationsEnabled)
        {
            rotation.BeginAnimation(
                RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(SpinnerRotationMilliseconds))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                });
        }
    }

    private void OnSpinnerUnloaded(object sender, RoutedEventArgs e) => StopSpinnerAnimation();

    /// <summary>
    /// Halts the arc's rotation. Besides the arc's own Unloaded this runs on load
    /// failure: a failed placeholder stays visible and must not keep implying progress.
    /// </summary>
    private void StopSpinnerAnimation()
    {
        if (_spinner?.RenderTransform is RotateTransform rotation)
        {
            rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // ActualWidth is still 0 while the first visual lines are built; recompute once
        // the control is in a laid-out tree (same approach as MarkdownTableControl).
        _textView.SizeChanged += OnTextViewSizeChanged;
        QueueSizeImage();
        if (_bitmap is not null || _loading || Child is Image)
        {
            return;
        }
        StartAsyncLoad();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _textView.SizeChanged -= OnTextViewSizeChanged;
        CancelQueuedSizeImage();
        _loadGeneration++;
        _loading = false;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        CloseToolbarNow();
    }

    private void OnTextViewSizeChanged(object sender, SizeChangedEventArgs args) => QueueSizeImage();

    private async void StartAsyncLoad()
    {
        _loading = true;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        int generation = ++_loadGeneration;
        try
        {
            MarkdownImageLoadResult result = await _loader.LoadAsync(ImageUri, _cancellation.Token);
            if (!result.IsSuccess || generation != _loadGeneration)
            {
                if (generation == _loadGeneration && !result.IsSuccess)
                {
                    StopSpinnerAnimation();
                }
                return;
            }
            // AvalonEdit can raise Loaded while the TextView is in its render pass, so
            // the size-changing mutation waits for the current layout pass to finish.
            await Dispatcher.InvokeAsync(
                () =>
                {
                    if (generation == _loadGeneration)
                    {
                        ApplyBitmap(result.Bitmap!);
                    }
                },
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (generation == _loadGeneration)
            {
                _loading = false;
            }
        }
    }

    private void ApplyBitmap(BitmapSource bitmap)
    {
        _bitmap = bitmap;
        StopSpinnerAnimation();
        if (_image is null)
        {
            _image = new Image
            {
                Stretch = Stretch.Uniform,
                Margin = new Thickness(ImageMargin)
            };
        }
        _image.Source = bitmap;
        MinWidth = 0;
        MinHeight = 0;
        // Establish a bounded size before putting the image into the visual tree.
        // This is important for cached images: when ActualWidth is not available
        // yet, SizeImage uses the provisional width instead of the raw pixel size.
        SizeImage();
        Child = _image;
        if (IsLoaded)
        {
            QueueSizeImage();
        }
    }

    private void QueueSizeImage()
    {
        if (_bitmap is null || _image is null || !IsLoaded || _textView.Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _sizeImageQueued = true;
        if (_sizeImageOperation is { Status: DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing })
        {
            return;
        }

        _sizeImageOperation = Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(ApplyQueuedImageSize));
    }

    private void ApplyQueuedImageSize()
    {
        _sizeImageOperation = null;
        if (!_sizeImageQueued || _bitmap is null || _image is null || !IsLoaded)
        {
            return;
        }

        _sizeImageQueued = false;
        SizeImage();
        if (_sizeImageQueued && _sizeImageOperation is null)
        {
            _sizeImageOperation = Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(ApplyQueuedImageSize));
        }
    }

    private void CancelQueuedSizeImage()
    {
        _sizeImageQueued = false;
        if (_sizeImageOperation is not null)
        {
            _sizeImageOperation.Abort();
            _sizeImageOperation = null;
        }
    }

    /// <summary>
    /// Sizes the image to its natural pixel size, or — when that does not fit the
    /// editor's text line — to the largest proportional size that does. With a width
    /// percentage set, the width is that share of the editor's text width instead,
    /// capped to the line, and the height follows the same scale factor.
    /// </summary>
    private void SizeImage()
    {
        if (_bitmap is null || _image is null)
        {
            return;
        }

        double chrome = BorderThickness.Left + BorderThickness.Right + ImageMargin * 2;
        // The control's own Margin is laid out around the border by the visual line, so
        // it must come out of the line width too — otherwise the footprint ends up wider
        // than one text line and paints over the scrollbar strip (same accounting as the
        // table control).
        double available = _textView.ActualWidth - chrome - Margin.Left - Margin.Right;
        double naturalWidth = Math.Max(1, _bitmap.PixelWidth);
        double naturalHeight = Math.Max(1, _bitmap.PixelHeight);
        // During AvalonEdit visual-line construction, or while a popout window is
        // morphing out of its dock strip, ActualWidth can briefly be zero/invalid.
        // Do not expose a cached bitmap's raw pixel width as DesiredSize in that
        // interval: a 3840px image would force a second line rebuild and feed the
        // width change back into TextView.
        if (!double.IsFinite(available) || available <= 0)
        {
            available = Math.Min(naturalWidth, PlaceholderWidth);
        }
        double scale;
        if (_span.ImageWidthPercent is { } percent && double.IsFinite(available) && available > 0)
        {
            double targetWidth = Math.Clamp(available * percent / 100.0, 1, available);
            scale = targetWidth / naturalWidth;
        }
        else
        {
            scale = double.IsFinite(available) && available > 0 && available < naturalWidth
                ? available / naturalWidth
                : 1;
        }
        double width = Math.Max(1, Math.Round(naturalWidth * scale));
        double height = Math.Max(1, Math.Round(naturalHeight * scale));
        if (_image.Width != width)
        {
            _image.Width = width;
        }
        if (_image.Height != height)
        {
            _image.Height = height;
        }
    }

    private void OnImageMouseEnter(object sender, MouseEventArgs e)
    {
        _toolbarCloseTimer.IsEnabled = false;
        OpenToolbar();
    }

    private void OnImageMouseLeave(object sender, MouseEventArgs e) => StartToolbarCloseTimer();

    private void StartToolbarCloseTimer()
    {
        if (_toolbar is { IsOpen: true })
        {
            _toolbarCloseTimer.IsEnabled = true;
        }
    }

    private void OpenToolbar()
    {
        if (_toolbar is null)
        {
            _toolbar = BuildToolbar();
        }
        RefreshToolbarSelection();
        if (_toolbarSurface is { } surface && _toolbarTranslate is { } translate)
        {
            if (!_toolbar.IsOpen)
            {
                // Start from the animation's initial pose so the popup never renders one
                // fully-visible frame before the fade-in begins.
                surface.Opacity = 0;
                translate.Y = ToolbarSlideOffset;
            }
            _toolbar.IsOpen = true;
            AnimateToolbarShow();
        }
        else
        {
            _toolbar.IsOpen = true;
        }
    }

    /// <summary>Animated close for hover paths; <see cref="CloseToolbarNow"/> closes instantly.</summary>
    private void CloseToolbar()
    {
        _toolbarCloseTimer.IsEnabled = false;
        if (_toolbar is { IsOpen: true })
        {
            AnimateToolbarHide();
        }
    }

    /// <summary>Closes the popup immediately, cancelling any running animation.</summary>
    private void CloseToolbarNow()
    {
        _toolbarCloseTimer.IsEnabled = false;
        _toolbarClosing = false;
        if (_toolbarSurface is { } surface)
        {
            surface.BeginAnimation(UIElement.OpacityProperty, null);
            surface.Opacity = 1;
        }
        if (_toolbarTranslate is { } translate)
        {
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = 0;
        }
        if (_toolbar is { IsOpen: true })
        {
            _toolbar.IsOpen = false;
        }
    }

    /// <summary>Fades the toolbar in while it rises into place; safe to run mid-hide.</summary>
    private void AnimateToolbarShow()
    {
        if (_toolbarSurface is not { } surface || _toolbarTranslate is not { } translate)
        {
            return;
        }
        _toolbarClosing = false;
        TimeSpan duration = TimeSpan.FromMilliseconds(ToolbarAnimationMilliseconds);
        DoubleAnimation fade = new(surface.Opacity, 1, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        DoubleAnimation slide = new(translate.Y, 0, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        surface.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>Fades the toolbar out while it drops away, then closes the popup.</summary>
    private void AnimateToolbarHide()
    {
        if (_toolbarSurface is not { } surface || _toolbarTranslate is not { } translate)
        {
            return;
        }
        _toolbarClosing = true;
        TimeSpan duration = TimeSpan.FromMilliseconds(ToolbarAnimationMilliseconds);
        DoubleAnimation fade = new(surface.Opacity, 0, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        DoubleAnimation slide = new(translate.Y, translate.Y + ToolbarSlideOffset, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            // A show animation started meanwhile replaces this clock and clears the flag,
            // so the popup must only close when the hide animation really finished.
            if (_toolbarClosing)
            {
                CloseToolbarNow();
            }
        };
        surface.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private Popup BuildToolbar()
    {
        StackPanel options = new() { Orientation = Orientation.Horizontal };
        options.Children.Add(CreateToolbarOption("自适应", null));
        foreach (double percent in ToolbarPercentOptions)
        {
            options.Children.Add(CreateToolbarOption($"{MarkdownImageWidthSyntax.Format(percent)}%", percent));
        }
        Border surface = new()
        {
            Child = options,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2),
            BorderThickness = new Thickness(1),
            RenderTransform = new TranslateTransform()
        };
        _toolbarSurface = surface;
        _toolbarTranslate = (TranslateTransform)surface.RenderTransform;
        surface.SetResourceReference(Border.BackgroundProperty, "SurfacePrimaryBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "BorderDefaultBrush");
        MemeMomo.UI.Text.LocalizeExtension.Set(surface, AutomationProperties.NameProperty, "图片宽度选项");
        surface.MouseEnter += (_, _) => _toolbarCloseTimer.IsEnabled = false;
        surface.MouseLeave += (_, _) => StartToolbarCloseTimer();

        return new Popup
        {
            Child = surface,
            // 以 TextView（而非图片控件）为放置目标：TextView 不随内容滚动，放置点换算
            // 到屏幕后位置稳定，滚动期间任何重排都甩不动工具栏。
            PlacementTarget = _textView,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = PlaceToolbar,
            StaysOpen = true,
            AllowsTransparency = true,
            Focusable = false
        };
    }

    /// <summary>
    /// Floats the toolbar over the image's bottom-left corner, a sliver above the image's
    /// bottom edge, left-aligned with it. While the image's bottom edge is scrolled out
    /// below the viewport the toolbar rests on the editor's visible bottom edge instead;
    /// scrolling then lifts it off together with the image's bottom edge (the glued and
    /// clamped positions coincide exactly at the crossing, so the hand-over is seamless).
    /// <para>
    /// The position is computed in TextView coordinates and the PlacementTarget is the
    /// TextView itself: the TextView never moves while content scrolls, so a re-placement
    /// that races the instant close — for example one triggered by WPF while the image has
    /// already scrolled out of view — maps to the very same screen spot. Once the image is
    /// fully outside the viewport the last computed position is frozen outright.
    /// </para>
    /// </summary>
    private CustomPopupPlacement[] PlaceToolbar(Size popupSize, Size targetSize, Point offset)
    {
        if (!TryGetAnchorState(out Point anchor, out Size viewport) ||
            !IsImageVisibleInViewport(anchor, viewport))
        {
            return new[] { new CustomPopupPlacement(_lastToolbarPlacement, PopupPrimaryAxis.None) };
        }
        double x = anchor.X;
        double y = anchor.Y + ActualHeight - popupSize.Height - ToolbarEdgeGap;
        if (TryGetVisibleBoundsInTextViewCoords(out Rect visible))
        {
            double minY = visible.Top + ToolbarEdgeGap;
            double maxY = visible.Bottom - ToolbarEdgeGap - popupSize.Height;
            y = Math.Clamp(y, minY, Math.Max(minY, maxY));
            double minX = visible.Left;
            double maxX = visible.Right - ToolbarEdgeGap - popupSize.Width;
            x = Math.Clamp(x, minX, Math.Max(minX, maxX));
        }
        _lastToolbarPlacement = new Point(x, y);
        return new[] { new CustomPopupPlacement(_lastToolbarPlacement, PopupPrimaryAxis.None) };
    }

    /// <summary>
    /// Computes the region where the editor actually shows content, in TextView
    /// coordinates: the TextView's own bounds intersected with the bounds of every
    /// clipping ancestor above it (a ScrollContentPresenter's Clip, ClipToBounds
    /// elements, …) mapped into the TextView's coordinate space.
    /// </summary>
    private bool TryGetVisibleBoundsInTextViewCoords(out Rect visible)
    {
        visible = new Rect(new Point(0, 0), _textView.RenderSize);
        bool found = visible.Width > 0 && visible.Height > 0;
        try
        {
            DependencyObject current = _textView;
            while (VisualTreeHelper.GetParent(current) is { } parent)
            {
                if (parent is not Visual parentVisual)
                {
                    break;
                }
                if (parentVisual is UIElement { } element)
                {
                    Rect? clip = element.ClipToBounds
                        ? new Rect(new Point(0, 0), element.RenderSize)
                        : null;
                    if (element.Clip is { } clipGeometry)
                    {
                        clip = clip.HasValue ? Rect.Intersect(clip.Value, clipGeometry.Bounds) : clipGeometry.Bounds;
                    }
                    if (clip is { } clipRect)
                    {
                        Rect mapped = element.TransformToDescendant(_textView).TransformBounds(clipRect);
                        if (mapped.IsEmpty)
                        {
                            return false;
                        }
                        visible = found ? Rect.Intersect(visible, mapped) : mapped;
                        found = true;
                    }
                }
                current = parent;
            }
        }
        catch (InvalidOperationException)
        {
            // The tree is not attached or not laid out; fall back to the unclipped bounds.
            return found;
        }
        return found && visible.Width > 0 && visible.Height > 0;
    }

    /// <summary>
    /// Keeps the open toolbar glued to the image while it moves (scrolling, re-layout):
    /// repositions the popup whenever the image's position or the visible area changes,
    /// and closes it immediately once the image has scrolled completely out of the
    /// editor's view — an animated close here would give WPF passes to re-place the popup
    /// while its anchor is gone, making it fly to a clamped spot elsewhere.
    /// LayoutUpdated fires for every layout pass on this dispatcher, so the early-outs
    /// keep the cost negligible while the toolbar is closed.
    /// </summary>
    private void OnControlLayoutUpdated(object? sender, EventArgs e)
    {
        if (_toolbar is not { IsOpen: true } toolbar ||
            !TryGetAnchorState(out Point anchor, out Size viewport))
        {
            return;
        }
        if (anchor == _lastToolbarAnchor && viewport == _lastToolbarViewport)
        {
            return;
        }
        _lastToolbarAnchor = anchor;
        _lastToolbarViewport = viewport;
        if (IsImageVisibleInViewport(anchor, viewport))
        {
            RepositionToolbar(toolbar);
        }
        else
        {
            CloseToolbarNow();
        }
    }

    /// <summary>
    /// Popup.Reposition is an internal API, so the placement callback is re-run by nudging
    /// HorizontalOffset — a placement-affecting property — instead. The callback ignores
    /// the offset value, so only the re-run matters; alternating the nudge direction keeps
    /// every call an actual property change.
    /// </summary>
    private void RepositionToolbar(Popup toolbar)
    {
        _toolbarNudgeFlip = !_toolbarNudgeFlip;
        toolbar.HorizontalOffset = _toolbarNudgeFlip ? ToolbarRepositionNudge : 0;
    }

    /// <summary>
    /// Captures the control's top-left corner in the TextView's viewport-relative
    /// coordinates together with the viewport size — the pair that determines both the
    /// glued toolbar position and whether the image is on screen at all.
    /// </summary>
    private bool TryGetAnchorState(out Point anchor, out Size viewport)
    {
        anchor = default;
        viewport = default;
        try
        {
            anchor = TransformToVisual(_textView).Transform(new Point(0, 0));
            viewport = _textView.RenderSize;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool IsImageVisibleInViewport(Point anchor, Size viewport) =>
        new Rect(anchor, new Size(ActualWidth, ActualHeight))
            .IntersectsWith(new Rect(new Point(0, 0), viewport));

    private Border CreateToolbarOption(string label, double? percent)
    {
        TextBlock text = new() { Text = label, FontSize = 10 };
        if (percent is null)
            MemeMomo.UI.Text.LocalizeExtension.Set(text, TextBlock.TextProperty, "自适应");
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Border option = new()
        {
            Child = text,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(1, 0, 1, 0),
            CornerRadius = new CornerRadius(3),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Tag = percent
        };
        MemeMomo.UI.Text.LocalizeExtension.Set(option, AutomationProperties.NameProperty,
            percent is null ? "图片宽度：自适应" : "图片宽度：{0}", label);
        option.MouseEnter += (_, _) =>
        {
            if (!IsActiveOption(option))
            {
                option.SetResourceReference(BackgroundProperty, "BgHoverBrush");
            }
        };
        option.MouseLeave += (_, _) => RefreshToolbarSelection();
        option.MouseLeftButtonUp += (_, _) =>
        {
            CloseToolbarNow();
            ApplyWidthPercent(percent);
        };
        _toolbarOptions.Add(option);
        return option;
    }

    private bool IsActiveOption(Border option) =>
        Nullable.Equals((double?)option.Tag, _span.ImageWidthPercent);

    private void RefreshToolbarSelection()
    {
        foreach (Border option in _toolbarOptions)
        {
            if (IsActiveOption(option))
            {
                option.SetResourceReference(BackgroundProperty, "AccentSubtleBrush");
            }
            else
            {
                option.SetResourceReference(BackgroundProperty, "TransparentBrush");
            }
            if (option.Child is TextBlock text)
            {
                text.FontWeight = IsActiveOption(option) ? FontWeights.Bold : FontWeights.Normal;
                text.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    IsActiveOption(option) ? "AccentPrimaryBrush" : "TextSecondaryBrush");
            }
        }
    }

    private MarkdownVisualSpan? FindCurrentSpan() => _adapter.Model.Spans.FirstOrDefault(span =>
        span.Kind == MarkdownVisualKind.Image &&
        span.SourceStart == _span.SourceStart &&
        span.SourceLength == _span.SourceLength &&
        string.Equals(span.ImageUri, _span.ImageUri, StringComparison.Ordinal));

    /// <summary>
    /// Rewrites the image's Markdown source so it carries (or drops) the ",NN%" width
    /// suffix; the projection rebuild then shows the new size. Source offsets captured at
    /// construction are re-validated against the live model first, so a stale control can
    /// never write into the wrong range.
    /// </summary>
    private void ApplyWidthPercent(double? percent)
    {
        if (FindCurrentSpan() is not { } image || image.ImageUri is null)
        {
            return;
        }
        string markdown = _adapter.Model.Markdown;
        string source = markdown.Substring(
            image.SourceStart,
            Math.Clamp(image.SourceLength, 0, markdown.Length - image.SourceStart));
        string next;
        if (source.StartsWith("![", StringComparison.Ordinal))
        {
            next = percent is { } value
                ? MarkdownImageWidthSyntax.WithPercent(source, value)
                : MarkdownImageWidthSyntax.StripSuffix(source);
        }
        else if (percent is { } htmlValue)
        {
            // An <img> source cannot carry the suffix, so a fixed width is expressed by
            // rewriting the safe image as the equivalent Markdown syntax.
            string alt = (image.AltText ?? string.Empty).Replace("[", "\\[").Replace("]", "\\]");
            next = MarkdownImageWidthSyntax.WithPercent($"![{alt}]({image.ImageUri})", htmlValue);
        }
        else
        {
            // An <img> without a percentage already renders adaptive; nothing to change.
            return;
        }
        if (next == source)
        {
            return;
        }
        string updated = markdown[..image.SourceStart] + next +
            markdown[(image.SourceStart + image.SourceLength)..];
        _adapter.ApplyEdit(new MarkdownEditResult(updated, image.SourceStart, image.SourceStart));
    }
}

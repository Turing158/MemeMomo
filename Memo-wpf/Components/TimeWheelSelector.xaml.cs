using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Memo.Infrastructure;
using Memo.UI;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using UserControl = System.Windows.Controls.UserControl;
using Window = System.Windows.Window;

namespace Memo.Components;

internal static class TimeWheelPhysics
{
    internal const double ViewportHeight = 96;
    internal const double ItemHeight = 32;
    internal const double RestingOpacity = 0.58;
    internal const double DragThreshold = 3;
    internal const double WheelNudge = 4;
    internal const double WheelVelocityImpulse = 220;
    internal const double MaximumVelocity = 1800;
    internal const double InertiaFriction = 7.5;
    internal const double SnapVelocity = 100;
    internal const double SnapSpring = 190;
    internal const double SnapDamping = 26;
    internal const double StopVelocity = 2;
    internal const double StopOffset = 0.2;
    internal const double MaximumFrameSeconds = 0.05;
    internal const double StaleDragVelocitySeconds = 0.12;
    internal const int BufferLeadDivisor = 4;

    internal static WheelPhysicsFrame Step(double offset, double velocity, bool snapping, double elapsedSeconds)
    {
        double seconds = Math.Clamp(elapsedSeconds, 0, MaximumFrameSeconds);
        if (!snapping && Math.Abs(velocity) > SnapVelocity)
        {
            double decay = Math.Exp(-InertiaFriction * seconds);
            double delta = velocity * (1 - decay) / InertiaFriction;
            double nextVelocity = velocity * decay;
            return new WheelPhysicsFrame(
                delta,
                nextVelocity,
                Math.Abs(nextVelocity) <= SnapVelocity,
                false);
        }

        double acceleration = (-SnapSpring * offset) - (SnapDamping * velocity);
        double snappedVelocity = velocity + (acceleration * seconds);
        double snappedDelta = snappedVelocity * seconds;
        bool stopped = Math.Abs(snappedVelocity) <= StopVelocity
            && Math.Abs(offset + snappedDelta) <= StopOffset;
        return new WheelPhysicsFrame(snappedDelta, snappedVelocity, true, stopped);
    }
}

internal readonly record struct WheelPhysicsFrame(double Delta, double Velocity, bool IsSnapping, bool ShouldStop);

public partial class TimeWheelSelector : UserControl
{
    private const double HalfItemHeight = TimeWheelPhysics.ItemHeight / 2;
    private const double SelectionTop = (TimeWheelPhysics.ViewportHeight - TimeWheelPhysics.ItemHeight) / 2;
    private const double ViewportCenter = TimeWheelPhysics.ViewportHeight / 2;

    private enum WheelPart { Hour, Minute, Second }

    private sealed class WheelStrip
    {
        internal WheelStrip(StackPanel panel, int range)
        {
            Panel = panel;
            Items = new TextBlock[range];
            for (int value = 0; value < range; value++)
            {
                TextBlock item = new()
                {
                    Text = value.ToString("D2"),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas")
                };
                item.SetResourceReference(TextBlock.ForegroundProperty, "AccentPrimaryBrush");
                panel.Children.Add(new Border { Height = TimeWheelPhysics.ItemHeight, Child = item });
                Items[value] = item;
            }
        }

        internal StackPanel Panel { get; }
        internal TextBlock[] Items { get; }
    }

    private sealed class WheelState
    {
        internal WheelState(Border track, StackPanel primaryStrip, StackPanel bufferStrip, int range)
        {
            Track = track;
            Range = range;
            Strips = [new WheelStrip(primaryStrip, range), new WheelStrip(bufferStrip, range)];
        }

        internal Border Track { get; }
        internal int Range { get; }
        internal int BufferLead => Math.Max(1, Range / TimeWheelPhysics.BufferLeadDivisor);
        internal double CycleHeight => Range * TimeWheelPhysics.ItemHeight;
        internal WheelStrip[] Strips { get; }
        internal int ActiveStripIndex { get; set; }
        internal int DisplayedValue { get; set; }
        internal int LastDirection { get; set; } = 1;
        internal double Offset { get; set; }
        internal double Velocity { get; set; }
        internal bool IsMotionRunning { get; set; }
        internal bool IsSnapping { get; set; }
        internal TimeSpan? LastFrameTimestamp { get; set; }
    }

    private readonly WheelState _hourWheel;
    private readonly WheelState _minuteWheel;
    private readonly WheelState _secondWheel;
    private WheelState? _pressedWheel;
    private WheelPart _pressedPart;
    private double _pressY;
    private double _lastPointerY;
    private long _lastPointerTimestamp;
    private double _dragVelocity;
    private bool _isDragging;
    private bool _pressedOnStepButton;
    private bool _suppressStepClick;
    private bool _renderingSubscribed;
    private IDisposable? _renderingLease;
    private Window? _ownerWindow;

    public TimeWheelSelector()
    {
        InitializeComponent();
        _hourWheel = new WheelState(HourTrack, HourPrimaryStrip, HourBufferStrip, 24);
        _minuteWheel = new WheelState(MinuteTrack, MinutePrimaryStrip, MinuteBufferStrip, 60);
        _secondWheel = new WheelState(SecondTrack, SecondPrimaryStrip, SecondBufferStrip, 60);
        AttachTrackHandlers(HourTrack);
        AttachTrackHandlers(MinuteTrack);
        AttachTrackHandlers(SecondTrack);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SetTime(TimeSpan.Zero);
    }

    public int Hour { get; private set; }
    public int Minute { get; private set; }
    public int Second { get; private set; }
    public TimeSpan SelectedTime => new(Hour, Minute, Second);

    public event EventHandler? SelectedTimeChanged;

    public void SetTime(TimeSpan time)
    {
        CancelPointerInteraction();
        Hour = Wrap(time.Hours, 24);
        Minute = Wrap(time.Minutes, 60);
        Second = Wrap(time.Seconds, 60);
        SetWheelImmediately(_hourWheel, Hour);
        SetWheelImmediately(_minuteWheel, Minute);
        SetWheelImmediately(_secondWheel, Second);
        StopRenderingIfIdle();
    }

    internal void AdjustHour(int delta) => Adjust(WheelPart.Hour, delta);
    internal void AdjustMinute(int delta) => Adjust(WheelPart.Minute, delta);
    internal void AdjustSecond(int delta) => Adjust(WheelPart.Second, delta);

    private void AttachTrackHandlers(Border track)
    {
        track.PreviewMouseLeftButtonDown += OnWheelMouseDown;
        track.PreviewMouseMove += OnWheelMouseMove;
        track.PreviewMouseLeftButtonUp += OnWheelMouseUp;
        track.LostMouseCapture += OnWheelLostMouseCapture;
        track.PreviewMouseWheel += OnWheelMouseWheel;
        track.PreviewKeyDown += OnWheelKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow is not null)
        {
            _ownerWindow.Deactivated += OnOwnerDeactivated;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelPointerInteraction();
        StopWheelMotion(_hourWheel, snapToCenter: true);
        StopWheelMotion(_minuteWheel, snapToCenter: true);
        StopWheelMotion(_secondWheel, snapToCenter: true);
        StopRendering();
        if (_ownerWindow is not null)
        {
            _ownerWindow.Deactivated -= OnOwnerDeactivated;
            _ownerWindow = null;
        }
    }

    private void OnOwnerDeactivated(object? sender, EventArgs e) => CancelPointerInteraction();

    private void OnStepClick(object sender, RoutedEventArgs e)
    {
        if (_suppressStepClick)
        {
            _suppressStepClick = false;
            e.Handled = true;
            return;
        }

        if (sender is Button { Tag: string tag } && TryParseStep(tag, out WheelPart part, out int delta))
        {
            Adjust(part, delta);
        }
    }

    private void OnWheelMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string tag } track || !TryParsePart(tag, out WheelPart part))
        {
            return;
        }

        CancelPointerInteraction();
        WheelState wheel = GetWheel(part);
        StopWheelMotion(wheel, snapToCenter: false);
        track.Focus();
        _pressedWheel = wheel;
        _pressedPart = part;
        _pressY = e.GetPosition(track).Y;
        _lastPointerY = _pressY;
        _lastPointerTimestamp = Stopwatch.GetTimestamp();
        _dragVelocity = 0;
        _isDragging = false;
        _pressedOnStepButton = _pressY < TimeWheelPhysics.ItemHeight
            || _pressY >= TimeWheelPhysics.ViewportHeight - TimeWheelPhysics.ItemHeight;
        _suppressStepClick = false;
        if (!_pressedOnStepButton)
        {
            track.CaptureMouse();
        }
    }

    private void OnWheelMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Border track || _pressedWheel is not { } wheel || !ReferenceEquals(wheel.Track, track))
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndPointerInteraction(startInertia: _isDragging);
            return;
        }

        double pointerY = e.GetPosition(track).Y;
        if (!_isDragging)
        {
            if (Math.Abs(pointerY - _pressY) < TimeWheelPhysics.DragThreshold)
            {
                return;
            }

            _isDragging = true;
            _suppressStepClick = _pressedOnStepButton;
            track.CaptureMouse();
        }

        long timestamp = Stopwatch.GetTimestamp();
        double delta = pointerY - _lastPointerY;
        UpdateDragVelocity(delta, timestamp);
        _lastPointerY = pointerY;
        _lastPointerTimestamp = timestamp;
        ApplyPixelDelta(_pressedPart, wheel, delta);
        e.Handled = true;
    }

    private void OnWheelMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedWheel is null)
        {
            return;
        }

        if (_isDragging && sender is Border track)
        {
            double pointerY = e.GetPosition(track).Y;
            long timestamp = Stopwatch.GetTimestamp();
            double delta = pointerY - _lastPointerY;
            UpdateDragVelocity(delta, timestamp);
            ApplyPixelDelta(_pressedPart, _pressedWheel, delta);
            e.Handled = true;
        }

        EndPointerInteraction(startInertia: _isDragging);
    }

    private void OnWheelLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_pressedWheel is not null && !ReferenceEquals(Mouse.Captured, _pressedWheel.Track))
        {
            EndPointerInteraction(startInertia: false);
        }
    }

    private void OnWheelMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Border { Tag: string tag } || !TryParsePart(tag, out WheelPart part) || e.Delta == 0)
        {
            return;
        }

        WheelState wheel = GetWheel(part);
        double delta = Math.Clamp(e.Delta / 120d, -3, 3);
        if (!MotionPreferences.AnimationsEnabled)
        {
            Adjust(part, delta > 0 ? -1 : 1);
        }
        else
        {
            ApplyPixelDelta(part, wheel, delta * TimeWheelPhysics.WheelNudge);
            wheel.Velocity = Math.Clamp(
                wheel.Velocity + (delta * TimeWheelPhysics.WheelVelocityImpulse),
                -TimeWheelPhysics.MaximumVelocity,
                TimeWheelPhysics.MaximumVelocity);
            wheel.IsSnapping = Math.Abs(wheel.Velocity) < TimeWheelPhysics.SnapVelocity;
            StartWheelMotion(wheel);
        }

        e.Handled = true;
    }

    private void OnWheelKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Border { Tag: string tag } || !TryParsePart(tag, out WheelPart part))
        {
            return;
        }

        int delta = e.Key switch
        {
            Key.Up or Key.Left => -1,
            Key.Down or Key.Right => 1,
            Key.PageUp => -5,
            Key.PageDown => 5,
            _ => 0
        };
        if (delta == 0)
        {
            return;
        }

        Adjust(part, delta);
        e.Handled = true;
    }

    private void Adjust(WheelPart part, int delta)
    {
        if (delta == 0)
        {
            return;
        }

        WheelState wheel = GetWheel(part);
        if (ReferenceEquals(_pressedWheel, wheel))
        {
            CancelPointerInteraction();
        }

        StopWheelMotion(wheel, snapToCenter: true);
        int direction = Math.Sign(delta);
        for (int index = 0; index < Math.Abs(delta); index++)
        {
            AdvanceWheel(wheel, direction);
        }

        UpdateSelectedValue(part, wheel.DisplayedValue);
        SetWheelFrame(wheel, 0);
        SelectedTimeChanged?.Invoke(this, EventArgs.Empty);
    }

    private WheelState GetWheel(WheelPart part) => part switch
    {
        WheelPart.Hour => _hourWheel,
        WheelPart.Minute => _minuteWheel,
        _ => _secondWheel
    };

    private void ApplyPixelDelta(WheelPart part, WheelState wheel, double delta)
    {
        if (Math.Abs(delta) < double.Epsilon)
        {
            return;
        }

        wheel.Offset += delta;
        wheel.LastDirection = delta < 0 ? 1 : -1;
        bool selectionChanged = false;
        while (wheel.Offset <= -HalfItemHeight)
        {
            wheel.Offset += TimeWheelPhysics.ItemHeight;
            AdvanceWheel(wheel, 1);
            selectionChanged = true;
        }

        while (wheel.Offset >= HalfItemHeight)
        {
            wheel.Offset -= TimeWheelPhysics.ItemHeight;
            AdvanceWheel(wheel, -1);
            selectionChanged = true;
        }

        SetWheelFrame(wheel, wheel.Offset);
        if (selectionChanged)
        {
            UpdateSelectedValue(part, wheel.DisplayedValue);
            SelectedTimeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void StartWheelMotion(WheelState wheel)
    {
        if (!MotionPreferences.AnimationsEnabled)
        {
            StopWheelMotion(wheel, snapToCenter: true);
            return;
        }

        wheel.IsMotionRunning = true;
        wheel.IsSnapping = Math.Abs(wheel.Velocity) < TimeWheelPhysics.SnapVelocity;
        wheel.LastFrameTimestamp = null;
        EnsureRendering();
    }

    private void EnsureRendering()
    {
        if (_renderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering += OnRendering;
        _renderingLease = UiResourceTracker.Acquire(UiResourceKind.RenderingSubscription);
        _renderingSubscribed = true;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs rendering)
        {
            return;
        }

        AdvanceWheelMotion(WheelPart.Hour, _hourWheel, rendering.RenderingTime);
        AdvanceWheelMotion(WheelPart.Minute, _minuteWheel, rendering.RenderingTime);
        AdvanceWheelMotion(WheelPart.Second, _secondWheel, rendering.RenderingTime);
        StopRenderingIfIdle();
    }

    private void AdvanceWheelMotion(WheelPart part, WheelState wheel, TimeSpan timestamp)
    {
        if (!wheel.IsMotionRunning)
        {
            return;
        }

        if (!MotionPreferences.AnimationsEnabled)
        {
            StopWheelMotion(wheel, snapToCenter: true);
            return;
        }

        if (wheel.LastFrameTimestamp is null)
        {
            wheel.LastFrameTimestamp = timestamp;
            return;
        }

        double seconds = (timestamp - wheel.LastFrameTimestamp.Value).TotalSeconds;
        wheel.LastFrameTimestamp = timestamp;
        WheelPhysicsFrame frame = TimeWheelPhysics.Step(wheel.Offset, wheel.Velocity, wheel.IsSnapping, seconds);
        wheel.Velocity = frame.Velocity;
        wheel.IsSnapping = frame.IsSnapping;
        ApplyPixelDelta(part, wheel, frame.Delta);
        if (frame.ShouldStop)
        {
            StopWheelMotion(wheel, snapToCenter: true);
        }
    }

    private void StopRenderingIfIdle()
    {
        if (!_hourWheel.IsMotionRunning && !_minuteWheel.IsMotionRunning && !_secondWheel.IsMotionRunning)
        {
            StopRendering();
        }
    }

    private void StopRendering()
    {
        if (!_renderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _renderingLease?.Dispose();
        _renderingLease = null;
        _renderingSubscribed = false;
    }

    private static void StopWheelMotion(WheelState wheel, bool snapToCenter)
    {
        wheel.IsMotionRunning = false;
        wheel.IsSnapping = false;
        wheel.LastFrameTimestamp = null;
        wheel.Velocity = 0;
        if (snapToCenter)
        {
            wheel.Offset = 0;
            SetWheelFrame(wheel, 0);
        }
    }

    private void EndPointerInteraction(bool startInertia)
    {
        if (_pressedWheel is null)
        {
            return;
        }

        WheelState wheel = _pressedWheel;
        double velocity = _dragVelocity;
        if (ElapsedSeconds(_lastPointerTimestamp, Stopwatch.GetTimestamp()) > TimeWheelPhysics.StaleDragVelocitySeconds)
        {
            velocity = 0;
        }

        _pressedWheel = null;
        _dragVelocity = 0;
        _isDragging = false;
        _pressedOnStepButton = false;
        if (Mouse.Captured is not null)
        {
            Mouse.Capture(null);
        }

        if (startInertia && MotionPreferences.AnimationsEnabled)
        {
            wheel.Velocity = Math.Clamp(velocity, -TimeWheelPhysics.MaximumVelocity, TimeWheelPhysics.MaximumVelocity);
            wheel.IsSnapping = Math.Abs(wheel.Velocity) < TimeWheelPhysics.SnapVelocity;
            StartWheelMotion(wheel);
        }
        else
        {
            StopWheelMotion(wheel, snapToCenter: true);
            StopRenderingIfIdle();
        }
    }

    private void CancelPointerInteraction()
    {
        if (_pressedWheel is null)
        {
            return;
        }

        WheelState wheel = _pressedWheel;
        _pressedWheel = null;
        _dragVelocity = 0;
        _isDragging = false;
        _pressedOnStepButton = false;
        _suppressStepClick = false;
        if (Mouse.Captured is not null)
        {
            Mouse.Capture(null);
        }

        StopWheelMotion(wheel, snapToCenter: true);
        StopRenderingIfIdle();
    }

    private void UpdateDragVelocity(double delta, long timestamp)
    {
        double elapsed = ElapsedSeconds(_lastPointerTimestamp, timestamp);
        if (elapsed <= 0.001 || Math.Abs(delta) < double.Epsilon)
        {
            return;
        }

        double instantaneous = delta / elapsed;
        double blend = Math.Clamp(elapsed * 18, 0.25, 0.7);
        _dragVelocity = Math.Clamp(
            _dragVelocity + ((instantaneous - _dragVelocity) * blend),
            -TimeWheelPhysics.MaximumVelocity,
            TimeWheelPhysics.MaximumVelocity);
    }

    private void UpdateSelectedValue(WheelPart part, int value)
    {
        switch (part)
        {
            case WheelPart.Hour: Hour = value; break;
            case WheelPart.Minute: Minute = value; break;
            case WheelPart.Second: Second = value; break;
        }
    }

    private static void AdvanceWheel(WheelState wheel, int direction)
    {
        bool crossesBoundary = direction > 0
            ? wheel.DisplayedValue == wheel.Range - 1
            : wheel.DisplayedValue == 0;
        if (crossesBoundary)
        {
            wheel.ActiveStripIndex = 1 - wheel.ActiveStripIndex;
        }

        wheel.DisplayedValue = Wrap(wheel.DisplayedValue + direction, wheel.Range);
        wheel.LastDirection = direction;
    }

    private static void SetWheelImmediately(WheelState wheel, int value)
    {
        wheel.IsMotionRunning = false;
        wheel.IsSnapping = false;
        wheel.LastFrameTimestamp = null;
        wheel.Offset = 0;
        wheel.Velocity = 0;
        wheel.ActiveStripIndex = 0;
        wheel.DisplayedValue = value;
        wheel.LastDirection = GetPreferredEdgeDirection(wheel, value, 1);
        SetWheelFrame(wheel, 0);
    }

    private static void SetWheelFrame(WheelState wheel, double offset)
    {
        WheelStrip activeStrip = wheel.Strips[wheel.ActiveStripIndex];
        WheelStrip bufferStrip = wheel.Strips[1 - wheel.ActiveStripIndex];
        double activeTop = SelectionTop - (wheel.DisplayedValue * TimeWheelPhysics.ItemHeight) + offset;
        double bufferTop = activeTop + (GetBufferDirection(wheel) * wheel.CycleHeight);
        SetStripPosition(activeStrip, activeTop);
        SetStripPosition(bufferStrip, bufferTop);
    }

    private static void SetStripPosition(WheelStrip strip, double top)
    {
        if (strip.Panel.RenderTransform is TranslateTransform transform)
        {
            transform.Y = top;
        }

        for (int index = 0; index < strip.Items.Length; index++)
        {
            double itemCenter = top + ((index + 0.5) * TimeWheelPhysics.ItemHeight);
            double distance = Math.Abs(itemCenter - ViewportCenter) / TimeWheelPhysics.ItemHeight;
            double emphasis = 1 - Math.Min(distance, 1);
            strip.Items[index].Opacity = TimeWheelPhysics.RestingOpacity
                + ((1 - TimeWheelPhysics.RestingOpacity) * emphasis);
        }
    }

    private static int GetBufferDirection(WheelState wheel) =>
        GetPreferredEdgeDirection(wheel, wheel.DisplayedValue, wheel.LastDirection);

    private static int GetPreferredEdgeDirection(WheelState wheel, int value, int fallback)
    {
        if (value <= wheel.BufferLead) return -1;
        if (value >= wheel.Range - 1 - wheel.BufferLead) return 1;
        return fallback == 0 ? 1 : Math.Sign(fallback);
    }

    private static bool TryParseStep(string value, out WheelPart part, out int delta)
    {
        string[] pieces = value.Split(':', 2);
        if (pieces.Length == 2 && TryParsePart(pieces[0], out part) && int.TryParse(pieces[1], out delta))
        {
            return true;
        }

        part = default;
        delta = 0;
        return false;
    }

    private static bool TryParsePart(string value, out WheelPart part) => Enum.TryParse(value, false, out part);
    private static double ElapsedSeconds(long start, long end) => Math.Max(0, end - start) / (double)Stopwatch.Frequency;
    private static int Wrap(int value, int range) => ((value % range) + range) % range;
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Memo.UI;
using System;
using System.Diagnostics;

namespace Memo.Components;

public partial class TimeWheelSelector : UserControl {
    private const double ViewportHeight = 96;
    private const double ItemHeight = 32;
    private const double HalfItemHeight = ItemHeight / 2;
    private const double SelectionTop = (ViewportHeight - ItemHeight) / 2;
    private const double ViewportCenter = ViewportHeight / 2;
    private const double RestingOpacity = 0.58;
    private const double DragThreshold = 3;
    private const double WheelNudge = 4;
    private const double WheelVelocityImpulse = 220;
    private const double MaximumVelocity = 1800;
    private const double InertiaFriction = 7.5;
    private const double SnapVelocity = 100;
    private const double SnapSpring = 190;
    private const double SnapDamping = 26;
    private const double StopVelocity = 2;
    private const double StopOffset = 0.2;
    private const double MaximumFrameSeconds = 0.05;
    private const double StaleDragVelocitySeconds = 0.12;
    // Stage the duplicate strip across a wide edge zone so it is already in
    // place before the selected value reaches either end of the number range.
    private const int BufferLeadDivisor = 4;

    private enum WheelPart { Hour, Minute, Second }

    private sealed class WheelStrip {
        public WheelStrip(StackPanel panel, int range) {
            Panel = panel;
            Items = new TextBlock[range];

            for (var value = 0; value < range; value++) {
                var item = new TextBlock {
                    Text = value.ToString("D2"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                };
                item.Classes.Add("TimeWheelNumber");

                panel.Children.Add(new Border {
                    Height = ItemHeight,
                    Child = item,
                });
                Items[value] = item;
            }
        }

        public StackPanel Panel { get; }
        public TextBlock[] Items { get; }
    }

    private sealed class WheelState {
        public WheelState(
            Border track,
            StackPanel primaryStrip,
            StackPanel bufferStrip,
            int range) {
            Track = track;
            Range = range;
            Strips = new[] {
                new WheelStrip(primaryStrip, range),
                new WheelStrip(bufferStrip, range),
            };
        }

        public Border Track { get; }
        public int Range { get; }
        public int BufferLead => Math.Max(1, Range / BufferLeadDivisor);
        public double CycleHeight => Range * ItemHeight;
        public WheelStrip[] Strips { get; }
        public int ActiveStripIndex { get; set; }
        public int DisplayedValue { get; set; }
        public int LastDirection { get; set; } = 1;
        public double Offset { get; set; }
        public double Velocity { get; set; }
        public bool IsMotionRunning { get; set; }
        public bool IsSnapping { get; set; }
        public long MotionGeneration { get; set; }
        public TimeSpan? LastFrameTimestamp { get; set; }
    }

    private readonly WheelState _hourWheel;
    private readonly WheelState _minuteWheel;
    private readonly WheelState _secondWheel;
    private WheelState? _pressedWheel;
    private WheelPart _pressedPart;
    private IPointer? _pressedPointer;
    private double _pressY;
    private double _lastPointerY;
    private long _lastPointerTimestamp;
    private double _dragVelocity;
    private bool _isDragging;
    private bool _pressedOnStepButton;
    private bool _suppressStepClick;
    private bool _isTransferringPointerCapture;

    public TimeWheelSelector() {
        InitializeComponent();

        _hourWheel = new WheelState(_hourTrack, _hourPrimaryStrip, _hourBufferStrip, 24);
        _minuteWheel = new WheelState(_minuteTrack, _minutePrimaryStrip, _minuteBufferStrip, 60);
        _secondWheel = new WheelState(_secondTrack, _secondPrimaryStrip, _secondBufferStrip, 60);

        SetTime(TimeSpan.Zero);
    }

    public int Hour { get; private set; }
    public int Minute { get; private set; }
    public int Second { get; private set; }
    public TimeSpan SelectedTime => new(Hour, Minute, Second);

    public event EventHandler? SelectedTimeChanged;

    public void SetTime(TimeSpan time) {
        CancelPointerInteraction();
        Hour = Wrap(time.Hours, 24);
        Minute = Wrap(time.Minutes, 60);
        Second = Wrap(time.Seconds, 60);

        SetWheelImmediately(_hourWheel, Hour);
        SetWheelImmediately(_minuteWheel, Minute);
        SetWheelImmediately(_secondWheel, Second);
    }

    internal void AdjustHour(int delta) => Adjust(WheelPart.Hour, delta);
    internal void AdjustMinute(int delta) => Adjust(WheelPart.Minute, delta);
    internal void AdjustSecond(int delta) => Adjust(WheelPart.Second, delta);

    private void OnStepClick(object? sender, RoutedEventArgs e) {
        if (_suppressStepClick) {
            _suppressStepClick = false;
            e.Handled = true;
            return;
        }

        if (sender is Button { Tag: string tag } && TryParseStep(tag, out var part, out var delta))
            Adjust(part, delta);
    }

    private void OnWheelPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (sender is not Border { Tag: string tag } track ||
            !TryParsePart(tag, out var part) ||
            !e.GetCurrentPoint(track).Properties.IsLeftButtonPressed)
            return;

        CancelPointerInteraction();
        var wheel = GetWheel(part);
        StopWheelMotion(wheel, snapToCenter: false);

        track.Focus(NavigationMethod.Pointer);
        _pressedWheel = wheel;
        _pressedPart = part;
        _pressedPointer = e.Pointer;
        _pressY = e.GetPosition(track).Y;
        _lastPointerY = _pressY;
        _lastPointerTimestamp = Stopwatch.GetTimestamp();
        _dragVelocity = 0;
        _isDragging = false;
        _pressedOnStepButton = _pressY < ItemHeight || _pressY >= ViewportHeight - ItemHeight;
        _suppressStepClick = false;

        // Let a step button keep its normal click capture until the pointer
        // actually starts dragging. The center row can capture immediately.
        if (!_pressedOnStepButton)
            e.Pointer.Capture(track);
    }

    private void OnWheelPointerMoved(object? sender, PointerEventArgs e) {
        if (sender is not Border track ||
            _pressedWheel is not { } wheel ||
            !ReferenceEquals(wheel.Track, track) ||
            !ReferenceEquals(_pressedPointer, e.Pointer))
            return;
        var part = _pressedPart;

        var point = e.GetCurrentPoint(track);
        if (!point.Properties.IsLeftButtonPressed) {
            EndPointerInteraction(startInertia: _isDragging);
            return;
        }

        var pointerY = point.Position.Y;
        if (!_isDragging) {
            if (Math.Abs(pointerY - _pressY) < DragThreshold) return;
            _isDragging = true;
            _suppressStepClick = _pressedOnStepButton;
            _isTransferringPointerCapture = true;
            try {
                e.Pointer.Capture(track);
            }
            finally {
                _isTransferringPointerCapture = false;
            }
            if (!ReferenceEquals(_pressedWheel, wheel)) return;
        }

        var timestamp = Stopwatch.GetTimestamp();
        var delta = pointerY - _lastPointerY;
        UpdateDragVelocity(delta, timestamp);
        _lastPointerY = pointerY;
        _lastPointerTimestamp = timestamp;
        ApplyPixelDelta(part, wheel, delta);
        e.Handled = true;
    }

    private void OnWheelPointerReleased(object? sender, PointerReleasedEventArgs e) {
        if (_pressedWheel is null || !ReferenceEquals(_pressedPointer, e.Pointer)) return;

        if (_isDragging && sender is Border track) {
            var pointerY = e.GetPosition(track).Y;
            var timestamp = Stopwatch.GetTimestamp();
            var delta = pointerY - _lastPointerY;
            UpdateDragVelocity(delta, timestamp);
            _lastPointerY = pointerY;
            _lastPointerTimestamp = timestamp;
            ApplyPixelDelta(_pressedPart, _pressedWheel, delta);
            e.Handled = true;
        }

        EndPointerInteraction(startInertia: _isDragging);
    }

    private void OnWheelPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) {
        if (_isTransferringPointerCapture) return;
        if (_pressedWheel is null || !ReferenceEquals(_pressedPointer, e.Pointer)) return;
        if (ReferenceEquals(e.Pointer.Captured, _pressedWheel.Track)) return;
        EndPointerInteraction(startInertia: false);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e) {
        if (sender is not Border { Tag: string tag } || !TryParsePart(tag, out var part)) return;
        if (Math.Abs(e.Delta.Y) < double.Epsilon) return;

        var wheel = GetWheel(part);
        var delta = Math.Clamp(e.Delta.Y, -3, 3);
        if (!MotionPreferences.AnimationsEnabled || TopLevel.GetTopLevel(this) is null) {
            Adjust(part, delta > 0 ? -1 : 1);
        }
        else {
            ApplyPixelDelta(part, wheel, delta * WheelNudge);
            wheel.Velocity = Math.Clamp(
                wheel.Velocity + (delta * WheelVelocityImpulse),
                -MaximumVelocity,
                MaximumVelocity);
            wheel.IsSnapping = Math.Abs(wheel.Velocity) < SnapVelocity;
            StartWheelMotion(part, wheel);
        }

        e.Handled = true;
    }

    private void OnWheelKeyDown(object? sender, KeyEventArgs e) {
        if (sender is not Control { Tag: string tag } || !TryParsePart(tag, out var part)) return;
        var delta = e.Key switch {
            Key.Up or Key.Left => -1,
            Key.Down or Key.Right => 1,
            Key.PageUp => -5,
            Key.PageDown => 5,
            _ => 0,
        };
        if (delta == 0) return;
        Adjust(part, delta);
        e.Handled = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        CancelPointerInteraction();
        SetWheelImmediately(_hourWheel, Hour);
        SetWheelImmediately(_minuteWheel, Minute);
        SetWheelImmediately(_secondWheel, Second);
        base.OnDetachedFromVisualTree(e);
    }

    private void Adjust(WheelPart part, int delta) {
        if (delta == 0) return;

        var wheel = GetWheel(part);
        if (ReferenceEquals(_pressedWheel, wheel)) CancelPointerInteraction();
        StopWheelMotion(wheel, snapToCenter: true);

        var direction = Math.Sign(delta);
        for (var index = 0; index < Math.Abs(delta); index++)
            AdvanceWheel(wheel, direction);
        UpdateSelectedValue(part, wheel.DisplayedValue);
        SetWheelFrame(wheel, 0);
        SelectedTimeChanged?.Invoke(this, EventArgs.Empty);
    }

    private WheelState GetWheel(WheelPart part) => part switch {
        WheelPart.Hour => _hourWheel,
        WheelPart.Minute => _minuteWheel,
        _ => _secondWheel,
    };

    private void ApplyPixelDelta(WheelPart part, WheelState wheel, double delta) {
        if (Math.Abs(delta) < double.Epsilon) return;

        wheel.Offset += delta;
        wheel.LastDirection = delta < 0 ? 1 : -1;
        var selectionChanged = false;

        while (wheel.Offset <= -HalfItemHeight) {
            wheel.Offset += ItemHeight;
            AdvanceWheel(wheel, 1);
            selectionChanged = true;
        }

        while (wheel.Offset >= HalfItemHeight) {
            wheel.Offset -= ItemHeight;
            AdvanceWheel(wheel, -1);
            selectionChanged = true;
        }

        SetWheelFrame(wheel, wheel.Offset);
        if (!selectionChanged) return;

        UpdateSelectedValue(part, wheel.DisplayedValue);
        SelectedTimeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartWheelMotion(WheelPart part, WheelState wheel) {
        if (!MotionPreferences.AnimationsEnabled || TopLevel.GetTopLevel(this) is not { } topLevel) {
            StopWheelMotion(wheel, snapToCenter: true);
            return;
        }

        if (wheel.IsMotionRunning) return;

        wheel.IsMotionRunning = true;
        wheel.IsSnapping = Math.Abs(wheel.Velocity) < SnapVelocity;
        wheel.LastFrameTimestamp = null;
        var generation = ++wheel.MotionGeneration;
        topLevel.RequestAnimationFrame(
            timestamp => OnWheelMotionFrame(part, wheel, topLevel, timestamp, generation));
    }

    private void OnWheelMotionFrame(
        WheelPart part,
        WheelState wheel,
        TopLevel topLevel,
        TimeSpan timestamp,
        long generation) {
        if (!wheel.IsMotionRunning || generation != wheel.MotionGeneration) return;
        if (!MotionPreferences.AnimationsEnabled) {
            StopWheelMotion(wheel, snapToCenter: true);
            return;
        }

        if (wheel.LastFrameTimestamp is null) {
            wheel.LastFrameTimestamp = timestamp;
            topLevel.RequestAnimationFrame(
                next => OnWheelMotionFrame(part, wheel, topLevel, next, generation));
            return;
        }

        var seconds = Math.Clamp(
            (timestamp - wheel.LastFrameTimestamp.Value).TotalSeconds,
            0,
            MaximumFrameSeconds);
        wheel.LastFrameTimestamp = timestamp;

        if (!wheel.IsSnapping && Math.Abs(wheel.Velocity) > SnapVelocity) {
            var decay = Math.Exp(-InertiaFriction * seconds);
            var delta = wheel.Velocity * (1 - decay) / InertiaFriction;
            wheel.Velocity *= decay;
            ApplyPixelDelta(part, wheel, delta);
            if (Math.Abs(wheel.Velocity) <= SnapVelocity)
                wheel.IsSnapping = true;
        }
        else {
            wheel.IsSnapping = true;
            var acceleration = (-SnapSpring * wheel.Offset) - (SnapDamping * wheel.Velocity);
            wheel.Velocity += acceleration * seconds;
            ApplyPixelDelta(part, wheel, wheel.Velocity * seconds);
        }

        if (!wheel.IsMotionRunning || generation != wheel.MotionGeneration) return;
        if (wheel.IsSnapping &&
            Math.Abs(wheel.Velocity) <= StopVelocity &&
            Math.Abs(wheel.Offset) <= StopOffset) {
            StopWheelMotion(wheel, snapToCenter: true);
            return;
        }

        topLevel.RequestAnimationFrame(
            next => OnWheelMotionFrame(part, wheel, topLevel, next, generation));
    }

    private static void StopWheelMotion(WheelState wheel, bool snapToCenter) {
        wheel.MotionGeneration++;
        wheel.IsMotionRunning = false;
        wheel.IsSnapping = false;
        wheel.LastFrameTimestamp = null;
        wheel.Velocity = 0;
        if (!snapToCenter) return;

        wheel.Offset = 0;
        SetWheelFrame(wheel, 0);
    }

    private void EndPointerInteraction(bool startInertia) {
        if (_pressedWheel is null || _pressedPointer is null) return;

        var wheel = _pressedWheel;
        var part = _pressedPart;
        var pointer = _pressedPointer;
        var track = wheel.Track;
        var velocity = _dragVelocity;
        if (ElapsedSeconds(_lastPointerTimestamp, Stopwatch.GetTimestamp()) > StaleDragVelocitySeconds)
            velocity = 0;

        _pressedWheel = null;
        _pressedPointer = null;
        _dragVelocity = 0;
        _isDragging = false;
        _pressedOnStepButton = false;

        if (ReferenceEquals(pointer.Captured, track))
            pointer.Capture(null);

        if (startInertia && MotionPreferences.AnimationsEnabled) {
            wheel.Velocity = Math.Clamp(velocity, -MaximumVelocity, MaximumVelocity);
            wheel.IsSnapping = Math.Abs(wheel.Velocity) < SnapVelocity;
            StartWheelMotion(part, wheel);
        }
        else {
            StopWheelMotion(wheel, snapToCenter: true);
        }
    }

    private void CancelPointerInteraction() {
        if (_pressedWheel is null || _pressedPointer is null) return;

        var wheel = _pressedWheel;
        var pointer = _pressedPointer;
        var track = wheel.Track;
        _pressedWheel = null;
        _pressedPointer = null;
        _dragVelocity = 0;
        _isDragging = false;
        _pressedOnStepButton = false;
        _suppressStepClick = false;
        if (ReferenceEquals(pointer.Captured, track))
            pointer.Capture(null);
        StopWheelMotion(wheel, snapToCenter: true);
    }

    private void UpdateDragVelocity(double delta, long timestamp) {
        var elapsed = ElapsedSeconds(_lastPointerTimestamp, timestamp);
        if (elapsed <= 0.001 || Math.Abs(delta) < double.Epsilon) return;

        var instantaneous = delta / elapsed;
        var blend = Math.Clamp(elapsed * 18, 0.25, 0.7);
        _dragVelocity = Math.Clamp(
            _dragVelocity + ((instantaneous - _dragVelocity) * blend),
            -MaximumVelocity,
            MaximumVelocity);
    }

    private void UpdateSelectedValue(WheelPart part, int value) {
        switch (part) {
            case WheelPart.Hour: Hour = value; break;
            case WheelPart.Minute: Minute = value; break;
            case WheelPart.Second: Second = value; break;
        }
    }

    private static void AdvanceWheel(WheelState wheel, int direction) {
        var crossesBoundary = direction > 0
            ? wheel.DisplayedValue == wheel.Range - 1
            : wheel.DisplayedValue == 0;
        if (crossesBoundary)
            wheel.ActiveStripIndex = 1 - wheel.ActiveStripIndex;

        wheel.DisplayedValue = Wrap(wheel.DisplayedValue + direction, wheel.Range);
        wheel.LastDirection = direction;
    }

    private static void SetWheelImmediately(WheelState wheel, int value) {
        wheel.MotionGeneration++;
        wheel.IsMotionRunning = false;
        wheel.IsSnapping = false;
        wheel.LastFrameTimestamp = null;
        wheel.Offset = 0;
        wheel.Velocity = 0;
        wheel.ActiveStripIndex = 0;
        wheel.DisplayedValue = value;
        wheel.LastDirection = GetPreferredEdgeDirection(wheel, value, fallback: 1);
        SetWheelFrame(wheel, 0);
    }

    private static void SetWheelFrame(WheelState wheel, double offset) {
        var activeStrip = wheel.Strips[wheel.ActiveStripIndex];
        var bufferStrip = wheel.Strips[1 - wheel.ActiveStripIndex];
        var activeTop = SelectionTop - (wheel.DisplayedValue * ItemHeight) + offset;
        var bufferDirection = GetBufferDirection(wheel);
        var bufferTop = activeTop + (bufferDirection * wheel.CycleHeight);

        SetStripPosition(activeStrip, activeTop);
        SetStripPosition(bufferStrip, bufferTop);
    }

    private static void SetStripPosition(WheelStrip strip, double top) {
        if (strip.Panel.RenderTransform is TranslateTransform transform)
            transform.Y = top;

        for (var index = 0; index < strip.Items.Length; index++) {
            var itemCenter = top + ((index + 0.5) * ItemHeight);
            var distanceFromCenter = Math.Abs(itemCenter - ViewportCenter) / ItemHeight;
            var emphasis = 1 - Math.Min(distanceFromCenter, 1);
            var item = strip.Items[index];
            item.Opacity = MotionAnimations.Lerp(RestingOpacity, 1, emphasis);
        }
    }

    private static int GetBufferDirection(WheelState wheel) =>
        GetPreferredEdgeDirection(wheel, wheel.DisplayedValue, wheel.LastDirection);

    private static int GetPreferredEdgeDirection(WheelState wheel, int value, int fallback) {
        if (value <= wheel.BufferLead) return -1;
        if (value >= wheel.Range - 1 - wheel.BufferLead) return 1;
        return fallback == 0 ? 1 : Math.Sign(fallback);
    }

    private static bool TryParseStep(string value, out WheelPart part, out int delta) {
        var pieces = value.Split(':', 2);
        if (pieces.Length == 2 && TryParsePart(pieces[0], out part) && int.TryParse(pieces[1], out delta))
            return true;
        part = default;
        delta = 0;
        return false;
    }

    private static bool TryParsePart(string value, out WheelPart part) =>
        Enum.TryParse(value, ignoreCase: false, out part);

    private static double ElapsedSeconds(long start, long end) =>
        Math.Max(0, end - start) / (double)Stopwatch.Frequency;

    private static int Wrap(int value, int range) => ((value % range) + range) % range;
}

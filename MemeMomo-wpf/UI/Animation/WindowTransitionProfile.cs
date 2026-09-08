namespace MemeMomo.UI.Animation;

internal sealed record WindowTransitionProfile(
    TimeSpan OpenDuration,
    TimeSpan CloseDuration,
    WindowTransitionFrame OpenStart,
    WindowTransitionFrame CloseEnd,
    MotionEasing OpenEasing,
    MotionEasing CloseEasing)
{
    internal static WindowTransitionProfile Default { get; } = new(
        TimeSpan.FromMilliseconds(190),
        TimeSpan.FromMilliseconds(150),
        WindowTransitionSampler.Open(0),
        WindowTransitionSampler.Close(1),
        MotionEasing.CubicEaseOut,
        MotionEasing.CubicEaseIn);

    // Native chrome stays at its final size so text and window edges fade together.
    internal static WindowTransitionProfile Panel { get; } = new(
        TimeSpan.FromMilliseconds(220),
        TimeSpan.FromMilliseconds(160),
        new WindowTransitionFrame(0, 1),
        new WindowTransitionFrame(0, 1),
        MotionEasing.CubicEaseOut,
        MotionEasing.CubicEaseInOut);
}

namespace Memo.UI.Animation;

internal readonly record struct WindowTransitionFrame(double Opacity, double Scale);

internal static class WindowTransitionSampler
{
    internal static WindowTransitionFrame Open(double progress)
    {
        double eased = FrameAnimationChannel.Sample(MotionEasing.CubicEaseOut, progress);
        return new WindowTransitionFrame(eased, Lerp(0.97, 1, eased));
    }

    internal static WindowTransitionFrame Close(double progress)
    {
        double eased = FrameAnimationChannel.Sample(MotionEasing.CubicEaseIn, progress);
        return new WindowTransitionFrame(1 - eased, Lerp(1, 0.985, eased));
    }

    internal static WindowTransitionFrame Interpolate(
        WindowTransitionFrame from,
        WindowTransitionFrame to,
        double progress) => new(
            Lerp(from.Opacity, to.Opacity, progress),
            Lerp(from.Scale, to.Scale, progress));

    private static double Lerp(double from, double to, double progress) =>
        from + (to - from) * progress;
}

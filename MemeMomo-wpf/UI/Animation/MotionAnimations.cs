using System.Runtime.CompilerServices;

namespace MemeMomo.UI.Animation;

internal static class MotionAnimations
{
    private sealed class Slot
    {
        internal FrameAnimation? Animation;
    }

    private static readonly ConditionalWeakTable<object, Slot> Slots = new();

    internal static void Start(object channel, TimeSpan duration, MotionEasing easing, Action<double> frame, Action? completed = null)
    {
        Slot slot = Slots.GetOrCreateValue(channel);
        slot.Animation?.Dispose();
        FrameAnimation? animation = null;
        animation = new FrameAnimation();
        slot.Animation = animation;
        animation.Start(duration, easing, frame, () =>
        {
            if (ReferenceEquals(slot.Animation, animation))
            {
                slot.Animation = null;
            }

            animation.Dispose();
            completed?.Invoke();
        });
    }

    internal static void Cancel(object channel)
    {
        if (Slots.TryGetValue(channel, out Slot? slot))
        {
            slot.Animation?.Dispose();
            slot.Animation = null;
        }
    }

    internal static bool IsRunning(object channel) =>
        Slots.TryGetValue(channel, out Slot? slot) && slot.Animation?.IsRunning == true;
}

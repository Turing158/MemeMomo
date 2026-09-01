using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using Memo.Infrastructure;
using Memo.Models;
using Memo.UI.Resources;

namespace Memo.UI;

internal interface ISystemMotionProbe : IDisposable
{
    event EventHandler? Changed;
    bool AnimationsEnabled { get; }
}

internal sealed class WindowsMotionProbe : ISystemMotionProbe
{
    private const uint SpiGetClientAreaAnimation = 0x1042;
    private int _disposed;
    public event EventHandler? Changed;
    public bool AnimationsEnabled => ReadAnimationsEnabled();

    internal WindowsMotionProbe() => SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            Changed = null;
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Accessibility or UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool ReadAnimationsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows() && NativeSystemParametersInfo(SpiGetClientAreaAnimation, 0, out int enabled, 0))
            {
                return enabled != 0;
            }
        }
        catch (DllNotFoundException)
        {
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return true;
        }

        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeSystemParametersInfo(uint action, uint parameter, out int value, uint flags);
}

internal static class MotionPreferences
{
    private static readonly TimeSpan FastDurationValue = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan StandardDurationValue = TimeSpan.FromMilliseconds(190);
    private static readonly TimeSpan DockDurationValue = TimeSpan.FromMilliseconds(220);
    private static System.Windows.Application? _application;
    private static ISystemMotionProbe? _probe;
    private static IDisposable? _probeLease;

    internal static event EventHandler? Changed;
    internal static MotionMode Mode { get; private set; } = MotionMode.AlwaysOn;
    internal static bool SystemAnimationsEnabled { get; private set; } = true;
    internal static bool AnimationsEnabled => Mode switch
    {
        MotionMode.AlwaysOn => true,
        MotionMode.FollowSystem => SystemAnimationsEnabled,
        MotionMode.Off => false,
        _ => true
    };
    internal static TimeSpan FastDuration => Effective(FastDurationValue);
    internal static TimeSpan StandardDuration => Effective(StandardDurationValue);
    internal static TimeSpan DockDuration => Effective(DockDurationValue);

    internal static void Initialize(System.Windows.Application application, ISystemMotionProbe? probe = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        Shutdown();
        VisualFoundationResources.EnsureLoaded(application);
        _application = application;
        _probe = probe ?? new WindowsMotionProbe();
        _probe.Changed += OnSystemPreferenceChanged;
        _probeLease = UiResourceTracker.Acquire(UiResourceKind.SystemEventsSubscription);
        SystemAnimationsEnabled = _probe.AnimationsEnabled;
        UpdateResources();
    }

    internal static void ApplyMode(MotionMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            mode = MotionMode.AlwaysOn;
        }

        if (_application is not null && !_application.Dispatcher.CheckAccess())
        {
            _application.Dispatcher.BeginInvoke(() => ApplyMode(mode));
            return;
        }

        bool oldEnabled = AnimationsEnabled;
        bool modeChanged = Mode != mode;
        Mode = mode;
        if (mode == MotionMode.FollowSystem && _probe is not null)
        {
            SystemAnimationsEnabled = _probe.AnimationsEnabled;
        }

        UpdateResources();
        if (modeChanged || oldEnabled != AnimationsEnabled)
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    internal static TimeSpan Effective(TimeSpan duration) => AnimationsEnabled ? duration : TimeSpan.Zero;

    internal static TimeSpan AdaptiveDuration(double distance, double distanceForMaximum = 400, double minimumMilliseconds = 120, double maximumMilliseconds = 220)
    {
        double ratio = Math.Clamp(distance / Math.Max(1, distanceForMaximum), 0, 1);
        return Effective(TimeSpan.FromMilliseconds(minimumMilliseconds + ((maximumMilliseconds - minimumMilliseconds) * ratio)));
    }

    internal static void RefreshSystemPreference()
    {
        if (_probe is not null)
        {
            bool previous = SystemAnimationsEnabled;
            SystemAnimationsEnabled = _probe.AnimationsEnabled;
            UpdateResources();
            if (previous != SystemAnimationsEnabled)
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }
    }

    internal static void Shutdown()
    {
        if (_probe is not null)
        {
            _probe.Changed -= OnSystemPreferenceChanged;
            _probe.Dispose();
        }

        _probe = null;
        _probeLease?.Dispose();
        _probeLease = null;
        _application = null;
        Changed = null;
    }

    private static void OnSystemPreferenceChanged(object? sender, EventArgs e)
    {
        if (_application is null)
        {
            return;
        }

        _application.Dispatcher.BeginInvoke(RefreshSystemPreference);
    }

    private static void UpdateResources()
    {
        if (_application is null)
        {
            return;
        }

        VisualFoundationResources.SetResource(_application, "MotionFastDuration", FastDuration);
        VisualFoundationResources.SetResource(_application, "MotionStandardDuration", StandardDuration);
        VisualFoundationResources.SetResource(_application, "MotionDockDuration", DockDuration);
    }
}

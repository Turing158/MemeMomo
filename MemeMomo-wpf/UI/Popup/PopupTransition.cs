using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using MemeMomo.UI.Animation;

namespace MemeMomo.UI.Popup;

internal static class PopupTransition
{
    private const double ClosedOffsetY = -2;
    private const double ClosedScale = 0.985;
    private static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(120);
    private static readonly ConditionalWeakTable<FrameworkElement, TransitionState> States = new();

    internal static void PrepareClosed(FrameworkElement target) =>
        GetState(target).PrepareClosed();

    internal static void Open(FrameworkElement target) =>
        GetState(target).Open();

    internal static void Close(FrameworkElement target, Action completed) =>
        GetState(target).Close(completed);

    internal static void MarkClosed(FrameworkElement target)
    {
        if (States.TryGetValue(target, out TransitionState? state))
        {
            state.MarkClosed();
        }
    }

    internal static void Detach(FrameworkElement target)
    {
        if (States.TryGetValue(target, out TransitionState? state))
        {
            state.Dispose();
            States.Remove(target);
        }
    }

    internal static PopupTransitionSnapshot Capture(FrameworkElement target) =>
        States.TryGetValue(target, out TransitionState? state)
            ? state.Capture()
            : new PopupTransitionSnapshot(target.Opacity, 0, false, false);

    private static TransitionState GetState(FrameworkElement target) =>
        States.GetValue(target, static key => new TransitionState(key));

    private sealed class TransitionState : IDisposable
    {
        private readonly FrameworkElement _target;
        private readonly object _originalOpacity;
        private readonly object _originalTransform;
        private readonly object _originalOrigin;
        private readonly object _originalCacheMode;
        private readonly object _originalHitTest;
        private readonly double _openOpacity;
        private readonly bool _openHitTestVisible;
        private readonly TranslateTransform _translation;
        private readonly ScaleTransform _scale;
        private readonly TransformGroup _motionTransform;
        private bool _presented;
        private bool _closing;
        private bool _ownsAnimationCache;
        private int _disposed;

        public TransitionState(FrameworkElement target)
        {
            _target = target;
            _originalOpacity = target.ReadLocalValue(UIElement.OpacityProperty);
            _originalTransform = target.ReadLocalValue(UIElement.RenderTransformProperty);
            _originalOrigin = target.ReadLocalValue(UIElement.RenderTransformOriginProperty);
            _originalCacheMode = target.ReadLocalValue(UIElement.CacheModeProperty);
            _originalHitTest = target.ReadLocalValue(UIElement.IsHitTestVisibleProperty);
            _openOpacity = target.Opacity;
            _openHitTestVisible = target.IsHitTestVisible;

            _motionTransform = new TransformGroup();
            Transform existing = target.RenderTransform;
            if (existing is not null && !ReferenceEquals(existing, Transform.Identity))
            {
                _motionTransform.Children.Add(existing);
            }

            _scale = new ScaleTransform(1, 1);
            _translation = new TranslateTransform();
            _motionTransform.Children.Add(_scale);
            _motionTransform.Children.Add(_translation);
            target.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            target.RenderTransform = _motionTransform;
        }

        internal void PrepareClosed()
        {
            ThrowIfDisposed();
            if (_presented)
            {
                return;
            }

            MotionAnimations.Cancel(this);
            Apply(0, ClosedScale, ClosedOffsetY);
            _target.IsHitTestVisible = false;
        }

        internal void Open()
        {
            ThrowIfDisposed();
            if (!_presented)
            {
                Apply(0, ClosedScale, ClosedOffsetY);
                _presented = true;
            }

            _closing = false;
            _target.IsHitTestVisible = _openHitTestVisible;
            AnimateTo(_openOpacity, 1, 0, MotionEasing.CubicEaseOut);
        }

        internal void Close(Action completed)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(completed);
            if (!_presented)
            {
                completed();
                return;
            }

            if (_closing)
            {
                return;
            }

            _closing = true;
            _target.IsHitTestVisible = false;
            AnimateTo(0, ClosedScale, ClosedOffsetY, MotionEasing.CubicEaseIn, () =>
            {
                if (!_closing)
                {
                    return;
                }

                _closing = false;
                _presented = false;
                Apply(0, ClosedScale, ClosedOffsetY);
                completed();
            });
        }

        internal void MarkClosed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            MotionAnimations.Cancel(this);
            EndAnimationCache();
            _closing = false;
            _presented = false;
            _target.IsHitTestVisible = false;
            Apply(0, ClosedScale, ClosedOffsetY);
        }

        internal PopupTransitionSnapshot Capture() =>
            new(_target.Opacity, _translation.Y, _presented, _closing);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            MotionAnimations.Cancel(this);
            EndAnimationCache();
            RestoreLocalValue(_target, UIElement.OpacityProperty, _originalOpacity);
            if (ReferenceEquals(_target.RenderTransform, _motionTransform))
            {
                RestoreLocalValue(_target, UIElement.RenderTransformProperty, _originalTransform);
                RestoreLocalValue(_target, UIElement.RenderTransformOriginProperty, _originalOrigin);
            }
            RestoreLocalValue(_target, UIElement.IsHitTestVisibleProperty, _originalHitTest);
        }

        private void AnimateTo(double opacity, double scale, double y, MotionEasing easing, Action? completed = null)
        {
            double fromOpacity = _target.Opacity;
            double fromScale = _scale.ScaleX;
            double fromY = _translation.Y;
            if (Math.Abs(fromOpacity - opacity) < 0.0001
                && Math.Abs(fromScale - scale) < 0.0001
                && Math.Abs(fromY - y) < 0.0001)
            {
                MotionAnimations.Cancel(this);
                Apply(opacity, scale, y);
                EndAnimationCache();
                completed?.Invoke();
                return;
            }

            BeginAnimationCache();
            MotionAnimations.Start(
                this,
                MotionPreferences.Effective(TransitionDuration),
                easing,
                progress => Apply(
                    fromOpacity + ((opacity - fromOpacity) * progress),
                    fromScale + ((scale - fromScale) * progress),
                    fromY + ((y - fromY) * progress)),
                () =>
                {
                    Apply(opacity, scale, y);
                    EndAnimationCache();
                    completed?.Invoke();
                });
        }

        private void BeginAnimationCache()
        {
            if (_ownsAnimationCache || _target.CacheMode is not null)
            {
                return;
            }

            _target.CacheMode = new BitmapCache
            {
                EnableClearType = true,
                SnapsToDevicePixels = false
            };
            _ownsAnimationCache = true;
        }

        private void EndAnimationCache()
        {
            if (!_ownsAnimationCache)
            {
                return;
            }

            RestoreLocalValue(_target, UIElement.CacheModeProperty, _originalCacheMode);
            _ownsAnimationCache = false;
        }

        private void Apply(double opacity, double scale, double y)
        {
            _target.Opacity = opacity;
            _scale.ScaleX = scale;
            _scale.ScaleY = scale;
            _translation.Y = y;
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        private static void RestoreLocalValue(DependencyObject target, DependencyProperty property, object value)
        {
            if (ReferenceEquals(value, DependencyProperty.UnsetValue))
            {
                target.ClearValue(property);
            }
            else
            {
                target.SetValue(property, value);
            }
        }
    }
}

internal readonly record struct PopupTransitionSnapshot(
    double Opacity,
    double OffsetY,
    bool IsPresented,
    bool IsClosing);

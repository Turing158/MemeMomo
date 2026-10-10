using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MemeMomo.Infrastructure;
using MemeMomo.UI;
using MemeMomo.UI.Animation;
using MemeMomo.UI.Notifications;
using UserControl = System.Windows.Controls.UserControl;
using Canvas = System.Windows.Controls.Canvas;
using Point = System.Windows.Point;

namespace MemeMomo.Components;

public partial class NotificationHost : UserControl, IDisposable
{
    private sealed class CardState(NotificationCard card)
    {
        internal NotificationCard Card { get; } = card;
        internal TranslateTransform Offset { get; } = new();
        internal ScaleTransform Scale { get; } = new();
        internal bool Exiting { get; set; }
        internal bool Entering { get; set; } = true;
        internal (double X, double Y, double Width, double Scale, double Opacity)? Target { get; set; }
    }

    private readonly Dictionary<NotificationMessage, CardState> _cards = [];
    private readonly object _scrollAnimation = new();
    private readonly DispatcherTimer _timer;
    private readonly IDisposable _timerLease;
    private bool _pointerOver;
    private bool _disposed;
    private double _targetHeight;
    private TaskCompletionSource<int>? _promptCompletion;
    private NotificationCard? _promptCard;
    private IInputElement? _promptFocus;
    internal bool HasPrompt => _promptCompletion is not null;
    internal NotificationQueue Queue { get; } = new();
    internal bool IsExpanded => _pointerOver || DeckViewport.IsKeyboardFocusWithin;

    public NotificationHost()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timerLease = UiResourceTracker.Acquire(UiResourceKind.DispatcherTimer);
        _timer.Tick += OnTimerTick;
        Queue.Changed += RefreshCards;
        SizeChanged += OnHostSizeChanged;
        Deck.SizeChanged += OnDeckSizeChanged;
        Unloaded += OnHostUnloaded;
        Loaded += OnHostLoaded;
    }

    internal NotificationMessage? Show(string message, NotificationKind kind = NotificationKind.Information,
        string? actionText = null, Func<Task<bool>>? action = null, object? key = null, bool persistent = false)
    {
        return _disposed ? null : Queue.Show(message, kind, actionText, action, key, persistent);
    }

    internal Task<int> ChooseAsync(string text, IReadOnlyList<string> choices)
    {
        if (_disposed || HasPrompt || !IsVisible || choices.Count == 0) return Task.FromResult(-1);
        _promptCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> task = _promptCompletion.Task;
        _promptFocus = Keyboard.FocusedElement;
        _promptCard = new NotificationCard();
        _promptCard.ConfigurePrompt(text, choices, choice =>
        {
            if (ReferenceEquals(task, _promptCompletion?.Task)) CompletePrompt(choice);
        });
        PromptViewport.Content = _promptCard;
        PromptShield.Visibility = PromptViewport.Visibility = Visibility.Visible;
        DeckViewport.Visibility = ExitLayer.Visibility = Visibility.Hidden;
        ResizePrompt();
        UpdateInteraction();
        _promptCard.PromptButtons[0].Focus();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (ReferenceEquals(task, _promptCompletion?.Task)) _promptCard?.PromptButtons[0].Focus();
        }));
        var offset = new TranslateTransform(0, -16);
        _promptCard.RenderTransform = offset;
        MotionAnimations.Start(PromptViewport, MotionPreferences.StandardDuration, MotionEasing.CubicEaseOut,
            progress => offset.Y = -16 * (1 - progress));
        return task;
    }

    private void ResizePrompt()
    {
        if (_promptCard is null) return;
        _promptCard.MaxWidth = 520;
        PromptViewport.MaxHeight = Math.Max(0, ActualHeight - 66);
    }

    internal void CompletePrompt(int choice)
    {
        if (_promptCompletion is not { } completion) return;
        _promptCompletion = null;
        MotionAnimations.Cancel(PromptViewport);
        PromptViewport.Content = null;
        _promptCard = null;
        PromptShield.Visibility = PromptViewport.Visibility = Visibility.Collapsed;
        ExitLayer.Visibility = Visibility.Visible;
        DeckViewport.Visibility = _cards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var focus = _promptFocus;
        _promptFocus = null;
        UpdateInteraction();
        if (IsVisible && focus is UIElement { IsVisible: true, IsEnabled: true } target) target.Focus();
        completion.TrySetResult(choice);
    }

    internal void CancelPrompt(Task<int> expectedPrompt)
    {
        if (ReferenceEquals(expectedPrompt, _promptCompletion?.Task)) CompletePrompt(-1);
    }

    internal bool HandlePromptKey(System.Windows.Input.KeyEventArgs e)
    {
        if (_promptCard is null) return false;
        var buttons = _promptCard.PromptButtons;
        int index = buttons.ToList().FindIndex(button => button.IsKeyboardFocusWithin);
        if (e.Key == Key.Escape) CompletePrompt(-1);
        else if (e.Key == Key.Tab)
        {
            int direction = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
            buttons[(Math.Max(0, index) + direction + buttons.Count) % buttons.Count].Focus();
        }
        else if (e.Key is Key.Enter or Key.Space) CompletePrompt(Math.Max(0, index));
        e.Handled = true;
        return true;
    }

    private void RefreshCards()
    {
        if (_disposed) return;
        foreach (var message in Queue.Messages)
        {
            if (!_cards.ContainsKey(message))
            {
                var previous = _cards.FirstOrDefault(pair => pair.Value.Exiting &&
                    (message.Key is null ? pair.Key.Key is null && pair.Key.Text == message.Text : ReferenceEquals(pair.Key.Key, message.Key)));
                if (previous.Value is not null)
                {
                    _cards.Remove(previous.Key);
                    _cards.Add(message, previous.Value);
                }
            }
            if (!_cards.TryGetValue(message, out var state))
            {
                var card = new NotificationCard();
                state = new CardState(card);
                var transforms = new TransformGroup();
                transforms.Children.Add(state.Scale);
                transforms.Children.Add(state.Offset);
                card.RenderTransform = transforms;
                card.Opacity = 0;
                card.DismissRequested += Queue.Dismiss;
                card.ActionRequested += OnActionRequested;
                _cards.Add(message, state);
                Deck.Children.Add(card);
            }
            if (state.Exiting)
            {
                // A duplicate can arrive during exit; bring the same visual back from
                // its current position instead of snapping or creating a second card.
                MoveCard(state, Deck);
                state.Exiting = false;
                state.Target = null;
            }
            state.Card.Update(message);
        }
        foreach (var state in _cards.Values.ToArray())
        {
            if (Queue.Messages.Contains(state.Card.Message) || state.Exiting) continue;
            state.Exiting = true;
            state.Card.IsHitTestVisible = false;
            MoveCard(state, ExitLayer);
            System.Windows.Controls.Panel.SetZIndex(state.Card, _cards.Count + 1);
            Animate(state, state.Offset.X, -state.Card.ActualHeight - 8, state.Card.Width, 1, 0, () => RemoveCard(state));
        }
        if (_cards.Count > 0 && !HasPrompt) DeckViewport.Visibility = Visibility.Visible;
        ArrangeCards();
        UpdateTimer();
    }

    private void ArrangeCards()
    {
        if (_disposed || Queue.Messages.Count == 0) return;
        DeckViewport.MaxHeight = Math.Max(0, ActualHeight - 66);
        double availableWidth = Math.Max(1, Math.Min(ActualWidth - 32, Deck.ActualWidth > 0 ? Deck.ActualWidth : ActualWidth - 32));
        double frontWidth = _cards[Queue.Messages[0]].Card.PreferredWidth(availableWidth);
        double top = 0;
        double firstHeight = 0;
        for (int index = 0; index < Queue.Messages.Count; index++)
        {
            var state = _cards[Queue.Messages[index]];
            double width = state.Card.PreferredWidth(availableWidth);
            if (!IsExpanded && index > 0) width = Math.Min(width, frontWidth);
            double currentWidth = state.Card.Width;
            state.Card.Width = width;
            state.Card.Measure(new System.Windows.Size(width, double.PositiveInfinity));
            double height = state.Card.DesiredSize.Height;
            double x = (availableWidth - width) / 2;
            if (state.Entering)
            {
                currentWidth = width;
                state.Offset.X = x;
                state.Offset.Y = -height - 16;
                state.Entering = false;
            }
            state.Card.Width = currentWidth;
            if (index == 0) firstHeight = height;
            // Only two card edges peek out. Hidden cards retain their own expiry/action.
            bool visible = IsExpanded || index < 3;
            // Keep outgoing stack layers visible until their opacity transition ends.
            if (visible) state.Card.Visibility = Visibility.Visible;
            state.Card.IsHitTestVisible = visible;
            state.Card.Focusable = false;
            state.Card.SetStacked(!IsExpanded && index > 0);
            System.Windows.Controls.Panel.SetZIndex(state.Card, Queue.Messages.Count - index);
            double y = IsExpanded ? top : index == 0 ? 0 : firstHeight - height + Math.Min(index, 2) * 8;
            Animate(state, x, y, width, IsExpanded ? 1 : 1 - Math.Min(index, 2) * 0.035, visible ? 1 : 0,
                visible ? null : () => state.Card.Visibility = Visibility.Hidden);
            top += height + 8;
        }
        double heightTarget = IsExpanded ? top - 8 : firstHeight + Math.Min(Queue.Messages.Count - 1, 2) * 8;
        if (Math.Abs(heightTarget - _targetHeight) > 0.1)
        {
            _targetHeight = heightTarget;
            double fromHeight = double.IsNaN(Deck.Height) ? heightTarget : Deck.Height;
            MotionAnimations.Start(Deck, MotionPreferences.Effective(TimeSpan.FromMilliseconds(260)), MotionEasing.CubicEaseInOut,
                progress => Deck.Height = fromHeight + (heightTarget - fromHeight) * progress);
        }
        if (IsExpanded) MotionAnimations.Cancel(_scrollAnimation);
        else if (DeckViewport.VerticalOffset > 0 && !MotionAnimations.IsRunning(_scrollAnimation))
        {
            double fromOffset = DeckViewport.VerticalOffset;
            MotionAnimations.Start(_scrollAnimation, MotionPreferences.Effective(TimeSpan.FromMilliseconds(260)), MotionEasing.CubicEaseInOut,
                progress => DeckViewport.ScrollToVerticalOffset(fromOffset * (1 - progress)));
        }
    }

    private void Animate(CardState state, double x, double y, double width, double scale, double opacity, Action? completed = null)
    {
        var target = (x, y, width, scale, opacity);
        if (state.Target == target) return;
        state.Target = target;
        double fromX = state.Offset.X, fromY = state.Offset.Y, fromWidth = state.Card.Width,
            fromScale = state.Scale.ScaleX, fromOpacity = state.Card.Opacity;
        bool exiting = state.Exiting;
        MotionAnimations.Start(state, MotionPreferences.Effective(TimeSpan.FromMilliseconds(exiting ? 280 : 260)),
            MotionEasing.CubicEaseInOut, progress =>
        {
            state.Offset.X = fromX + (x - fromX) * progress;
            state.Offset.Y = fromY + (y - fromY) * progress;
            state.Card.Width = fromWidth + (width - fromWidth) * progress;
            state.Scale.ScaleX = fromScale + (scale - fromScale) * progress;
            // Retain a readable card during the upward slide; fade only near the edge.
            double fade = exiting ? Math.Clamp((progress - 0.7) / 0.3, 0, 1) : progress;
            state.Card.Opacity = fromOpacity + (opacity - fromOpacity) * fade;
            UpdateHoverSurface();
        }, completed);
    }

    private void MoveCard(CardState state, Canvas destination)
    {
        MotionAnimations.Cancel(state);
        var source = (Canvas)state.Card.Parent;
        Point position = state.Card.TranslatePoint(new Point(), destination);
        source.Children.Remove(state.Card);
        state.Offset.X = position.X - (1 - state.Scale.ScaleX) * state.Card.ActualWidth / 2;
        state.Offset.Y = position.Y;
        destination.Children.Add(state.Card);
    }

    private void UpdateHoverSurface()
    {
        // Keep the gaps hoverable without letting a short bubble block the entire row.
        var visible = _cards.Values.Where(state => !state.Exiting && state.Card.Opacity > 0).ToArray();
        double left = visible.Length == 0 ? 0 : visible.Min(state => state.Offset.X);
        double right = visible.Length == 0 ? 0 : visible.Max(state => state.Offset.X + state.Card.Width);
        // Include the path to the scrollbar while expanded so crossing the gap
        // between a narrow card and the scrollbar does not collapse the deck.
        double hitWidth = right - left + 8;
        if (DeckViewport.ComputedVerticalScrollBarVisibility == Visibility.Visible)
            hitWidth = Math.Max(hitWidth, DeckViewport.ActualWidth - left);
        DeckClip.Rect = new Rect(Math.Max(0, left), 0,
            Math.Max(0, hitWidth), DeckViewport.ActualHeight);
    }

    private void RemoveCard(CardState state)
    {
        ((Canvas)state.Card.Parent).Children.Remove(state.Card);
        state.Card.StopAnimations();
        _cards.Remove(state.Card.Message);
        UpdateHoverSurface();
        if (_cards.Count == 0)
        {
            DeckViewport.Visibility = Visibility.Collapsed;
            _pointerOver = false;
            Queue.SetPaused(HasPrompt || !IsVisible);
        }
    }

    private async void OnActionRequested(NotificationMessage message)
    {
        try { await Queue.InvokeActionAsync(message); }
        catch (Exception error) { Show(error.Message, NotificationKind.Error); }
    }

    private void OnTimerTick(object? sender, EventArgs e) => Queue.Expire();
    private void UpdateTimer()
    {
        if (!_disposed && IsVisible && Queue.Messages.Count > 0 && !Queue.IsPaused) _timer.Start();
        else _timer.Stop();
    }

    private void UpdateInteraction()
    {
        Queue.SetPaused(HasPrompt || IsExpanded || !IsVisible);
        ArrangeCards();
        UpdateTimer();
    }

    private void OnDeckMouseEnter(object sender, System.Windows.Input.MouseEventArgs e) { _pointerOver = true; UpdateInteraction(); }
    private void OnDeckMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) { _pointerOver = false; UpdateInteraction(); }
    private void OnDeckFocusChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateInteraction();
    private void OnHostVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_timer is null || _disposed) return;
        if (!IsVisible) { _pointerOver = false; CompletePrompt(-1); }
        UpdateInteraction();
    }
    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e) { ArrangeCards(); ResizePrompt(); }
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) => UpdateHoverSurface();
    private void OnDeckSizeChanged(object sender, SizeChangedEventArgs e) { if (e.WidthChanged) ArrangeCards(); }
    private void OnHostLoaded(object sender, RoutedEventArgs e) { RefreshCards(); UpdateInteraction(); }
    private void OnHostUnloaded(object sender, RoutedEventArgs e)
    {
        CompletePrompt(-1);
        _timer.Stop();
        Queue.SetPaused(true);
        MotionAnimations.Cancel(Deck);
        MotionAnimations.Cancel(_scrollAnimation);
        Deck.Height = _targetHeight;
        foreach (var state in _cards.Values)
        {
            MotionAnimations.Cancel(state);
            state.Target = null;
            state.Card.StopAnimations();
        }
        foreach (var state in _cards.Values.Where(state => state.Exiting).ToArray())
        {
            RemoveCard(state);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CompletePrompt(-1);
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _timerLease.Dispose();
        Queue.Changed -= RefreshCards;
        Queue.Clear();
        MotionAnimations.Cancel(Deck);
        MotionAnimations.Cancel(_scrollAnimation);
        foreach (var state in _cards.Values)
        {
            MotionAnimations.Cancel(state);
            state.Card.StopAnimations();
        }
        _cards.Clear();
        Deck.Children.Clear();
        ExitLayer.Children.Clear();
        DeckViewport.Visibility = Visibility.Collapsed;
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using MemeMomo.Services;
using MemeMomo.UI;
using MemeMomo.UI.Animation;
using MemeMomo.UI.Notifications;
using UserControl = System.Windows.Controls.UserControl;

namespace MemeMomo.Components;

public partial class NotificationCard : UserControl
{
    private readonly object _contentAnimation = new();
    private double _contentTarget = 1;
    internal NotificationMessage Message { get; private set; } = null!;
    internal event Action<NotificationMessage>? DismissRequested;
    internal event Action<NotificationMessage>? ActionRequested;

    public NotificationCard() => InitializeComponent();

    internal void ConfigurePrompt(string text, IReadOnlyList<string> choices, Action<int> choose)
    {
        DismissButton.Visibility = ActionButton.Visibility = Visibility.Collapsed;
        PromptSurface.Visibility = Visibility.Visible;
        PromptText.Text = text;
        for (int index = 0; index < choices.Count; index++)
        {
            int result = index;
            var button = new System.Windows.Controls.Button
            {
                Content = choices[index],
                Margin = new Thickness(4, 3, 4, 0),
                Padding = new Thickness(12, 6, 12, 6),
                MinHeight = 32
            };
            AutomationProperties.SetName(button, choices[index]);
            button.Click += (_, e) => { e.Handled = true; choose(result); };
            PromptActions.Children.Add(button);
        }
    }

    internal IReadOnlyList<System.Windows.Controls.Button> PromptButtons =>
        PromptActions.Children.OfType<System.Windows.Controls.Button>().ToArray();

    internal void Update(NotificationMessage message)
    {
        Message = message;
        MessageText.Text = message.Text;
        AutomationProperties.SetName(DismissButton, message.Text + " · " + LocalizationService.Get("收起提示"));
        bool hasAction = message.Action is not null && !string.IsNullOrEmpty(message.ActionText);
        ActionButton.Content = message.ActionText;
        ActionButton.Visibility = hasAction ? Visibility.Visible : Visibility.Collapsed;
        ActionButton.IsEnabled = DismissButton.IsEnabled = !message.IsBusy;
        ActionButton.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double gutter = Math.Max(34, hasAction ? ActionButton.DesiredSize.Width - DismissButton.Padding.Right + 8 : 0);
        LeadingGutter.Width = TrailingGutter.Width = new GridLength(gutter);
        string color = message.Kind switch { NotificationKind.Success => "Success", NotificationKind.Error => "Danger", _ => "Accent" };
        KindBadge.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, color + "SubtleBrush");
        KindIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, color + "PrimaryBrush");
        KindIcon.Data = Geometry.Parse(message.Kind switch
        {
            NotificationKind.Success => "M 2,6 L 5,9 L 11,3",
            NotificationKind.Error => "M 6,1 L 6,7 M 6,9 L 6,10",
            _ => "M 6,1 L 6,2 M 6,4 L 6,10"
        });
    }

    internal double PreferredWidth(double availableWidth)
    {
        // Measure the unwrapped text without changing the live TextBlock's layout.
        var text = new FormattedText(MessageText.Text, LocalizationService.Culture, MessageText.FlowDirection,
            new Typeface(MessageText.FontFamily, MessageText.FontStyle, MessageText.FontWeight, MessageText.FontStretch),
            MessageText.FontSize, MessageText.Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double chrome = LeadingGutter.Width.Value + TrailingGutter.Width.Value
            + DismissButton.Padding.Left + DismissButton.Padding.Right
            + DismissButton.BorderThickness.Left + DismissButton.BorderThickness.Right;
        return Math.Min(availableWidth, Math.Ceiling(text.WidthIncludingTrailingWhitespace + chrome));
    }

    internal void SetStacked(bool stacked)
    {
        double target = stacked ? 0 : 1;
        if (_contentTarget != target)
        {
            _contentTarget = target;
            double from = MessageText.Opacity;
            MotionAnimations.Start(_contentAnimation, MotionPreferences.StandardDuration, MotionEasing.CubicEaseInOut,
                progress => MessageText.Opacity = KindBadge.Opacity = ActionButton.Opacity = from + (target - from) * progress);
        }
        ActionButton.IsHitTestVisible = !stacked;
        ActionButton.Focusable = !stacked;
    }

    internal void StopAnimations()
    {
        MotionAnimations.Cancel(_contentAnimation);
        _contentTarget = double.NaN;
    }

    private void OnDismissClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        DismissRequested?.Invoke(Message);
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ActionRequested?.Invoke(Message);
    }
}

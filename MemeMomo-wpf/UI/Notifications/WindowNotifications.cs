using System.Windows;
using MemeMomo.Components;
using MemeMomo.Services;
using MemeMomo.UI.Windows;

namespace MemeMomo.UI.Notifications;

internal static class WindowNotifications
{
    internal static NotificationHost? FindHost(DependencyObject source)
    {
        var window = source as Window ?? Window.GetWindow(source);
        if (window?.FindName("Notifications") is NotificationHost existing) return existing;
        return (window as BorderlessWindow)?.GetNotificationHost();
    }

    internal static void Show(DependencyObject source, string text, NotificationKind kind = NotificationKind.Information) =>
        FindHost(source)?.Show(text, kind);

    internal static Task<int> ChooseAsync(DependencyObject source, string text, params string[] choices) =>
        FindHost(source)?.ChooseAsync(text, choices) ?? Task.FromResult(-1);

    internal static async Task<bool> ConfirmAsync(DependencyObject source, string title, string message) =>
        await ChooseAsync(source, title + "\n" + message,
            LocalizationService.Get("取消"), LocalizationService.Get("确定")) == 1;
}

// One feedback channel belongs to one operation/field, not to the whole window.
internal sealed class NotificationFeedback(DependencyObject source)
{
    private NotificationHost? _host;
    private NotificationMessage? _message;
    internal string Text { get; private set; } = string.Empty;

    internal void Show(string text, NotificationKind kind = NotificationKind.Information, bool persistent = false,
        string? actionText = null, Func<Task<bool>>? action = null)
    {
        Text = text;
        var host = WindowNotifications.FindHost(source);
        if (host is null) return;
        if (!ReferenceEquals(host, _host)) ClearMessage();
        _host = host;
        if (_message is not null && host.Queue.Messages.Contains(_message) && _message.Text == text
            && _message.Kind == kind && _message.Persistent == persistent
            && _message.ActionText == actionText && _message.Action == action) return;
        _message = host.Show(text, kind, actionText, action, this, persistent);
    }

    internal void Clear()
    {
        Text = string.Empty;
        ClearMessage();
    }

    private void ClearMessage()
    {
        if (_message is not null) _host?.Queue.Dismiss(_message);
        _message = null;
        _host = null;
    }
}

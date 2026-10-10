using System.Diagnostics;

namespace MemeMomo.UI.Notifications;

internal enum NotificationKind { Information, Success, Error }

internal sealed class NotificationMessage(string text)
{
    internal string Text { get; set; } = text;
    internal object? Key { get; set; }
    internal bool Persistent { get; set; }
    internal NotificationKind Kind { get; set; }
    internal string? ActionText { get; set; }
    internal Func<Task<bool>>? Action { get; set; }
    internal TimeSpan ExpiresAt { get; set; }
    internal bool IsBusy { get; set; }
    internal int Revision { get; set; }
}

// Each message has its own deadline. A shared UI timer only checks those deadlines.
internal sealed class NotificationQueue(Func<TimeSpan>? clock = null)
{
    internal static readonly TimeSpan DisplayDuration = TimeSpan.FromSeconds(5);
    private readonly Func<TimeSpan> _clock = clock ?? (() => Stopwatch.GetElapsedTime(0));
    private readonly List<NotificationMessage> _messages = [];
    internal IReadOnlyList<NotificationMessage> Messages => _messages;
    internal bool IsPaused { get; private set; }
    internal event Action? Changed;

    internal NotificationMessage Show(string text, NotificationKind kind = NotificationKind.Information,
        string? actionText = null, Func<Task<bool>>? action = null, object? key = null, bool persistent = false)
    {
        text = text.Trim();
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var message = _messages.FirstOrDefault(item => key is null
            ? item.Key is null && item.Text == text : ReferenceEquals(item.Key, key));
        if (message is null)
        {
            message = new NotificationMessage(text);
            _messages.Insert(0, message);
        }
        else
        {
            _messages.Remove(message);
            _messages.Insert(0, message);
        }
        message.Kind = kind;
        message.Text = text;
        message.Key = key;
        message.Persistent = persistent;
        message.ActionText = actionText;
        message.Action = action;
        message.Revision++;
        message.ExpiresAt = _clock() + DisplayDuration;
        Changed?.Invoke();
        return message;
    }

    internal void SetPaused(bool paused)
    {
        if (IsPaused == paused) return;
        IsPaused = paused;
        foreach (var message in _messages) message.ExpiresAt = _clock() + DisplayDuration;
        Changed?.Invoke();
    }

    internal void Expire()
    {
        if (IsPaused) return;
        var now = _clock();
        if (_messages.RemoveAll(message => !message.Persistent && !message.IsBusy && message.ExpiresAt <= now) > 0)
            Changed?.Invoke();
    }

    internal void Dismiss(NotificationMessage message)
    {
        if (!message.IsBusy && _messages.Remove(message)) Changed?.Invoke();
    }

    internal async Task InvokeActionAsync(NotificationMessage message)
    {
        if (message.IsBusy || !_messages.Contains(message) || message.Action is not { } action) return;
        int revision = message.Revision;
        message.IsBusy = true;
        Changed?.Invoke();
        try
        {
            if (await action() && message.Revision == revision) _messages.Remove(message);
        }
        finally
        {
            message.IsBusy = false;
            message.ExpiresAt = _clock() + DisplayDuration;
            Changed?.Invoke();
        }
    }

    internal void Clear()
    {
        _messages.Clear();
        IsPaused = false;
        Changed?.Invoke();
    }
}

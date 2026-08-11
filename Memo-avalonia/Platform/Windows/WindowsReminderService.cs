using Memo.Markdown;
using Memo.Models;
using Microsoft.Toolkit.Uwp.Notifications;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Memo.Platform.Windows;

/// <summary>Windows 计划通知桥接；通知参数只携带备忘录 ID。</summary>
public sealed class WindowsReminderService : IDisposable {
    private const string MemoIdArgument = "memoId";
    private readonly object _activationGate = new();
    private Guid? _pendingActivation;
    private Exception? _initializationError;
    private bool _isActivationRegistered;
    private bool _disposed;

    private static readonly Lazy<WindowsReminderService> SharedInstance =
        new(() => new WindowsReminderService());

    public static WindowsReminderService Shared => SharedInstance.Value;

    public event Action<Guid>? Activated;

    private WindowsReminderService() {
        TryInitializeActivation();
    }

    /// <summary>应在 Avalonia 启动前调用，确保系统激活能够尽早注册。</summary>
    public static void InitializeActivation() => _ = Shared;

    public void Schedule(MemoItem memo, DateTime reminderAt) {
        EnsureInitialized();
        if (reminderAt <= DateTime.Now)
            throw new ArgumentOutOfRangeException(nameof(reminderAt), "提醒时间必须晚于当前时间。");

        Cancel(memo.Id);

        var title = string.IsNullOrWhiteSpace(memo.Title) ? "备忘录" : memo.Title;
        var body = MarkdownNotificationText.BuildBody(memo.Content, title);
        var builder = new ToastContentBuilder()
            .AddArgument(MemoIdArgument, memo.Id.ToString("D"))
            .AddText(title);
        if (!string.IsNullOrWhiteSpace(body)) builder.AddText(body);

        var deliveryTime = new DateTimeOffset(DateTime.SpecifyKind(reminderAt, DateTimeKind.Local));
        builder.Schedule(deliveryTime, toast => toast.Id = GetNotificationId(memo.Id));
    }

    public void Cancel(Guid memoId) {
        EnsureInitialized();
        var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
        var id = GetNotificationId(memoId);
        foreach (var scheduled in notifier.GetScheduledToastNotifications()
                     .Where(item => string.Equals(item.Id, id, StringComparison.Ordinal))
                     .ToArray()) {
            notifier.RemoveFromSchedule(scheduled);
        }
    }

    public void Reconcile(IEnumerable<MemoItem> memos) {
        foreach (var memo in memos.Where(item => item.ReminderAt.HasValue)) {
            try {
                if (memo.ReminderAt > DateTime.Now) Schedule(memo, memo.ReminderAt.Value);
            }
            catch (Exception ex) {
                Debug.WriteLine($"[Reminder] Reconcile failed for {memo.Id}: {ex.Message}");
            }
        }
    }

    public bool TryTakePendingActivation(out Guid memoId) {
        lock (_activationGate) {
            if (_pendingActivation is not { } pending) {
                memoId = Guid.Empty;
                return false;
            }

            memoId = pending;
            _pendingActivation = null;
            return true;
        }
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat args) {
        if (!TryParseMemoId(args.Argument, out var memoId)) return;

        lock (_activationGate) _pendingActivation = memoId;
        Activated?.Invoke(memoId);
    }

    internal static bool TryParseMemoId(string? argument, out Guid memoId) {
        memoId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(argument)) return false;
        try {
            var arguments = ToastArguments.Parse(argument);
            return arguments.TryGetValue(MemoIdArgument, out string value) &&
                   Guid.TryParse(value, out memoId);
        }
        catch {
            return false;
        }
    }

    private static string GetNotificationId(Guid memoId) =>
        // Some Windows 10 builds reject the documented 16-character maximum.
        memoId.ToString("N")[..15];

    private void EnsureInitialized() {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsReminderService));
        if (_isActivationRegistered || TryInitializeActivation()) return;
        throw new InvalidOperationException("Windows 通知服务无法注册。", _initializationError);
    }

    private bool TryInitializeActivation() {
        if (_isActivationRegistered || _disposed) return _isActivationRegistered;
        try {
            ToastNotificationManagerCompat.OnActivated += OnToastActivated;
            _isActivationRegistered = true;
            _initializationError = null;
            return true;
        }
        catch (Exception ex) {
            _initializationError = ex;
            Debug.WriteLine($"[Reminder] Activation registration failed: {ex.Message}");
            return false;
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        if (_isActivationRegistered) {
            ToastNotificationManagerCompat.OnActivated -= OnToastActivated;
            _isActivationRegistered = false;
        }
    }
}

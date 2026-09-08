namespace MemeMomo.Services;

/// <summary>
/// Shell 气泡的投递出口，由 <see cref="ReminderToastQueue"/> 在节拍到达时调用。
/// 实现须自行兜住 Shell 的异常与差异，不得把失败抛回队列节拍。
/// </summary>
public interface IBalloonNotificationSink
{
    void ShowBalloon(string title, string body);
}

/// <summary>
/// 逐条串行的提醒气泡队列：同一时刻最多一条活动条目，靠看门狗推进而不依赖气泡关闭事件
/// （Win10/11 下气泡进入通知中心后不保证回传关闭事件）。时间一律由调用方经 <see cref="Pump"/>
/// 传入，不持有定时器、线程或任何 UI 类型，事件全部发生在创建方线程上，因此无需加锁。
/// </summary>
public sealed class ReminderToastQueue
{
    /// <summary>活动条目的显示秒数，超过即被看门狗视为结束。</summary>
    internal const int DisplaySeconds = 7;

    /// <summary>上一条结束与下一条投递之间的最小间隔秒数，避免 Shell 合并相邻气泡。</summary>
    internal const int GapSeconds = 1;

    /// <summary>等待队列上限；溢出时 Enqueue 返回 false，由调用方对该条改走应用内弹窗。</summary>
    internal const int MaximumQueuedEntries = 32;

    /// <summary>NOTIFYICONDATA szInfoTitle 的可用长度（64 字符含终止符）。</summary>
    internal const int MaxTitleLength = 63;

    /// <summary>NOTIFYICONDATA szInfo 的可用长度（256 字符含终止符）。</summary>
    internal const int MaxBodyLength = 255;

    internal const string FallbackTitle = "备忘录提醒";
    internal const string FallbackBody = "点击查看便签";

    private const char Ellipsis = '…';

    private readonly List<Entry> _entries = [];
    private Entry? _active;
    private DateTime _shownAt;
    private DateTime? _lastEndedAt;

    // 点击/关闭事件不携带时间戳，结束时刻推迟到下一次 Pump 记账，以保证 GapSeconds 节拍可测。
    private bool _recordEndAtNextPump;

    public IBalloonNotificationSink? Sink { get; set; }

    /// <summary>等待投递的条目数（不含活动条目）。</summary>
    internal int Count => _entries.Count;

    /// <summary>等待投递条目的 memoId，按投递顺序。</summary>
    internal IReadOnlyList<Guid> QueuedMemoIds => _entries.Select(item => item.MemoId).ToArray();

    /// <summary>当前活动条目的 memoId；无活动条目时为 null（含看门狗结束后才到达的迟到点击）。</summary>
    internal Guid? ActiveMemoId => _active?.MemoId;

    /// <summary>当前活动条目的标题（已归一化）。</summary>
    internal string? ActiveTitle => _active?.Title;

    /// <summary>
    /// 入队一条提醒。同一 memoId 已在等待队列或正处于活动状态时，仅更新标题与正文、位置不变；
    /// 等待队列已满时返回 false（条目不入队），由调用方对该条改走应用内弹窗兜底。
    /// </summary>
    public bool Enqueue(Guid memoId, string title, string body)
    {
        Entry entry = new(memoId, NormalizeTitle(title), NormalizeBody(body));
        int index = _entries.FindIndex(item => item.MemoId == memoId);
        if (index >= 0)
        {
            _entries[index] = entry;
            return true;
        }

        if (_active is not null && _active.MemoId == memoId)
        {
            _active = entry;
            return true;
        }

        if (_entries.Count >= MaximumQueuedEntries)
        {
            return false;
        }

        _entries.Add(entry);
        return true;
    }

    /// <summary>移除指定 memo 的提醒；命中活动条目时清空活动态，下一条仍需等 GapSeconds。</summary>
    public void Cancel(Guid memoId)
    {
        _entries.RemoveAll(item => item.MemoId == memoId);
        if (_active is not null && _active.MemoId == memoId)
        {
            _active = null;
            _recordEndAtNextPump = true;
        }
    }

    /// <summary>每次节拍调用一次：先按看门狗推进活动条目，再在间隔满足后向 sink 投递队首。</summary>
    public void Pump(DateTime now)
    {
        if (_active is not null && now - _shownAt >= TimeSpan.FromSeconds(DisplaySeconds))
        {
            EndActive(_shownAt + TimeSpan.FromSeconds(DisplaySeconds));
        }

        if (_recordEndAtNextPump)
        {
            _recordEndAtNextPump = false;
            _lastEndedAt = now;
        }

        if (_active is not null || _entries.Count == 0 || Sink is null)
        {
            return;
        }

        if (_lastEndedAt is { } endedAt && now - endedAt < TimeSpan.FromSeconds(GapSeconds))
        {
            return;
        }

        Entry entry = _entries[0];
        _entries.RemoveAt(0);
        _active = entry;
        _shownAt = now;
        Sink.ShowBalloon(entry.Title, entry.Body);
    }

    /// <summary>
    /// 气泡被点击时调用：返回活动条目的 memoId 并结束该条目。
    /// 无活动条目（含看门狗结束后的迟到点击）时返回 null，且不消耗后续条目。
    /// </summary>
    public Guid? NotifyClicked()
    {
        if (_active is null)
        {
            return null;
        }

        Guid memoId = _active.MemoId;
        EndActiveBetweenPumps();
        return memoId;
    }

    /// <summary>气泡被用户关闭或超时消失时调用。</summary>
    public void NotifyDismissed()
    {
        if (_active is not null)
        {
            EndActiveBetweenPumps();
        }
    }

    /// <summary>清空全部条目与活动态，用于退出时与托盘销毁对称。</summary>
    public void Clear()
    {
        _entries.Clear();
        _active = null;
        _lastEndedAt = null;
        _recordEndAtNextPump = false;
    }

    private void EndActive(DateTime endedAt)
    {
        _active = null;
        _lastEndedAt = endedAt;
    }

    private void EndActiveBetweenPumps()
    {
        _active = null;
        _recordEndAtNextPump = true;
    }

    internal static string NormalizeTitle(string? title) => Normalize(title, FallbackTitle, MaxTitleLength);

    internal static string NormalizeBody(string? body) => Normalize(body, FallbackBody, MaxBodyLength);

    /// <summary>
    /// 空文本回落兜底文案（正文为空时 Shell 不显示气泡）；超长按 char 截断并补省略号，
    /// 截断点落在代理对中间时回退一个 char，避免把 emoji / 部分 CJK 扩展切坏。
    /// </summary>
    private static string Normalize(string? value, string fallback, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return LocalizationService.Get(fallback);
        }

        if (value.Length <= maxLength)
        {
            return value;
        }

        int keep = maxLength - 1;
        if (char.IsHighSurrogate(value[keep - 1]) && char.IsLowSurrogate(value[keep]))
        {
            keep--;
        }

        return value[..keep] + Ellipsis;
    }

    private sealed record Entry(Guid MemoId, string Title, string Body);
}

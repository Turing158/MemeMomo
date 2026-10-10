using System;
using System.ComponentModel;
using System.Text.Json.Serialization;
using MemeMomo.Markdown;
using MemeMomo.Utils;

namespace MemeMomo.Models;

/// <summary>
/// 单条备忘录模型。Title / Subtitle 由 Content 自动派生（首行=标题，第二行起=副标题）。
/// </summary>
public class MemoItem : INotifyPropertyChanged
{
    private string _content = string.Empty;
    private DateTime _createdAt;
    private DateTime _updatedAt;
    private DateTime? _reminderAt;
    private MemoKind _kind;
    private string? _taskStatusId;
    private TaskStatusCategory? _taskStatusCategorySnapshot;
    private DateTime? _taskClosedAt;
    private string? _lastActiveTaskStatusId;
    private bool _isTaskCommitPending;
    private DateTimeOffset? _deletedAt;
    private bool _isLifecyclePending;

    public DateTimeOffset? DeletedAt
    {
        get => _deletedAt;
        set
        {
            if (_deletedAt == value) return;
            _deletedAt = value?.ToUniversalTime();
            OnPropertyChanged(nameof(DeletedAt));
            OnPropertyChanged(nameof(IsDeleted));
            OnPropertyChanged(nameof(CanRemind));
            RefreshLocalizedText();
        }
    }
    [JsonIgnore] public bool IsDeleted => DeletedAt is not null;
    [JsonIgnore] public long LifecycleVersion { get; internal set; }
    [JsonIgnore]
    public bool IsLifecyclePending
    {
        get => _isLifecyclePending;
        internal set { _isLifecyclePending = value; OnPropertyChanged(nameof(IsLifecyclePending)); OnPropertyChanged(nameof(CanRemind)); }
    }

    [JsonConverter(typeof(MemoKindConverter))]
    public MemoKind Kind { get => _kind; set { if (_kind != value) { _kind = value; TaskFieldChanged(nameof(Kind)); } } }
    [JsonConverter(typeof(TaskReferenceConverter))]
    public string? TaskStatusId { get => _taskStatusId; set { if (_taskStatusId != value) { _taskStatusId = value; TaskFieldChanged(nameof(TaskStatusId)); } } }
    [JsonConverter(typeof(TaskCategorySnapshotConverter))]
    public TaskStatusCategory? TaskStatusCategorySnapshot { get => _taskStatusCategorySnapshot; set { if (_taskStatusCategorySnapshot != value) { _taskStatusCategorySnapshot = value; TaskFieldChanged(nameof(TaskStatusCategorySnapshot)); } } }
    [JsonConverter(typeof(TaskClosedAtConverter))]
    public DateTime? TaskClosedAt { get => _taskClosedAt; set { if (_taskClosedAt != value) { _taskClosedAt = value; OnPropertyChanged(nameof(TaskClosedAt)); } } }
    [JsonConverter(typeof(TaskReferenceConverter))]
    public string? LastActiveTaskStatusId { get => _lastActiveTaskStatusId; set { if (_lastActiveTaskStatusId != value) { _lastActiveTaskStatusId = value; OnPropertyChanged(nameof(LastActiveTaskStatusId)); } } }
    [JsonIgnore]
    public bool IsTask => Kind == MemoKind.Task;
    [JsonIgnore]
    public bool IsTaskClosed => IsTask && TaskStatusCategorySnapshot is TaskStatusCategory.Done or TaskStatusCategory.Canceled;
    [JsonIgnore]
    public bool CanRemind => !IsDeleted && !IsLifecyclePending && !IsTaskClosed && !IsTaskCommitPending;
    [JsonIgnore]
    public bool IsTaskCommitPending { get => _isTaskCommitPending; internal set { if (_isTaskCommitPending != value) { _isTaskCommitPending = value; TaskFieldChanged(nameof(IsTaskCommitPending)); } } }

    private void TaskFieldChanged(string property)
    {
        OnPropertyChanged(property);
        OnPropertyChanged(nameof(IsTask));
        OnPropertyChanged(nameof(IsTaskClosed));
        OnPropertyChanged(nameof(CanRemind));
    }

    internal MemoItem PersistenceCopy() => new()
    {
        Id = Id,
        Content = Content,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        ReminderAt = ReminderAt,
        DeletedAt = DeletedAt,
        Kind = Kind,
        TaskStatusId = TaskStatusId,
        TaskStatusCategorySnapshot = TaskStatusCategorySnapshot,
        TaskClosedAt = TaskClosedAt,
        LastActiveTaskStatusId = LastActiveTaskStatusId
    };

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Content
    {
        get => _content;
        set
        {
            if (_content != value)
            {
                _content = value;
                OnPropertyChanged(nameof(Content));
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public DateTime CreatedAt
    {
        get => _createdAt;
        set
        {
            if (_createdAt != value)
            {
                _createdAt = value;
                OnPropertyChanged(nameof(CreatedAt));
            }
        }
    }

    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set
        {
            if (_updatedAt != value)
            {
                _updatedAt = value;
                OnPropertyChanged(nameof(UpdatedAt));
            }
        }
    }

    /// <summary>计划提醒的本地时间；null 表示当前没有提醒。</summary>
    public DateTime? ReminderAt
    {
        get => _reminderAt;
        set
        {
            if (_reminderAt != value)
            {
                _reminderAt = value;
                OnPropertyChanged(nameof(ReminderAt));
            }
        }
    }

    /// <summary>关联的弹出窗口引用计数（新建新窗体 +1，关闭 -1），不持久化。</summary>
    [JsonIgnore]
    public int PopoutRefCount { get; set; }

    // —— 派生显示字段 ——

    /// <summary>相对时间文本（今天/昨天/周内/同年/跨年）。</summary>
    [JsonIgnore]
    public string RelativeTime
    {
        get => (DeletedAt?.LocalDateTime ?? CreatedAt).ToRelativeTimeString();
    }

    /// <summary>完整时间文本（今天/昨天/周内/同年/跨年）。</summary>
    [JsonIgnore]
    public string FullTime
    {
        get => (DeletedAt?.LocalDateTime ?? CreatedAt).ToFullTimeString();
    }

    [JsonIgnore] public string Title => MarkdownSummary.GetTitle(_content);

    /// <summary>副标题：仅取内容的第二行（trim 后），为空则返回空字符串。</summary>
    [JsonIgnore] public string Subtitle => MarkdownSummary.GetSubtitle(_content);

    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(RelativeTime));
        OnPropertyChanged(nameof(FullTime));
        OnPropertyChanged(nameof(Title));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

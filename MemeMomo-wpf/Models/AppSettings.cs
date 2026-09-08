using System.Text.Json.Serialization;

namespace MemeMomo.Models;

public class AppSettings
{
    public const int MinimumMainWindowDockSize = 30;
    public const int MaximumMainWindowDockSize = 75;
    public const int DefaultMainWindowDockSize = 44;

    public AppLanguage Language { get; set; } = AppLanguage.ChineseSimplified;
    public ThemeMode ThemeMode { get; set; } = ThemeMode.FollowSystem;
    public MotionMode MotionMode { get; set; } = MotionMode.FollowSystem;
    public CloseButtonAction CloseButtonAction { get; set; } = CloseButtonAction.MinimizeToTray;
    public bool HasAskedCloseButtonAction { get; set; }
    public HotkeySetting ToggleTopmostHotkey { get; set; } = new() { Key = "T", Ctrl = true, Alt = true };
    public HotkeySetting ToggleMemoTaskbarHotkey { get; set; } = new() { Key = "B", Ctrl = true, Alt = true };
    public HotkeySetting MinimizeHotkey { get; set; } = new() { Key = "M", Ctrl = true, Alt = true };
    public HotkeySetting ShowWindowHotkey { get; set; } = new() { Key = "N", Ctrl = true, Alt = true };
    public HotkeySetting QuickMemoHotkey { get; set; } = new() { Key = "C", Ctrl = true, Alt = true };
    public bool QuickMemoEnabled { get; set; } = true;
    /// <summary>重复便签：关闭时如果已存在相同备忘录的窗体则移动位置，开启时总是创建新窗体。</summary>
    public bool DuplicateMemoEnabled { get; set; }
    /// <summary>托盘图标单击显示主界面。默认 false，使用双击显示（与旧版行为一致）。</summary>
    public bool TraySingleClickToShow { get; set; }
    /// <summary>主界面窗口显示时是否在 Windows 任务栏中显示图标。沿用旧 JSON 键以兼容已有设置。</summary>
    [JsonPropertyName("showTaskbarIcon")]
    public bool ShowMainWindowTaskbarIcon { get; set; }
    /// <summary>是否在独立便签标题栏显示任务栏图标开关。开启后每个窗口默认显示图标。</summary>
    public bool ShowMemoWindowTaskbarIcon { get; set; }
    /// <summary>快速添加后自动显示便签：依赖 QuickMemoEnabled，仅在启用快速粘贴时才生效。</summary>
    public bool QuickMemoShowPopoutAfterAdd { get; set; } = true;
    /// <summary>新建便签窗口默认置顶。已有便签的置顶状态由用户在窗口上单独控制，不持久化。</summary>
    public bool MemoWindowTopmostByDefault { get; set; } = true;
    /// <summary>提醒到期时的投递方式：系统通知 / 应用内窗口 / 两者。旧 settings.json 无此键时回落 SystemToast。</summary>
    public ReminderNotificationMode ReminderNotification { get; set; } = ReminderNotificationMode.SystemToast;
    /// <summary>便签贴边：拖动便签到屏幕左右边缘时收起为半圆标签。默认开启，旧 settings.json 无此键时同样视为开启。</summary>
    public bool PopoutDockEnabled { get; set; } = true;

    public bool MainWindowDockEnabled { get; set; } = true;
    public bool MainWindowDocked { get; set; }
    public int MainWindowDockSize { get; set; } = DefaultMainWindowDockSize;
    public MainWindowDockEdge MainWindowDockEdge { get; set; } = MainWindowDockEdge.Left;
    public double MainWindowDockPosition { get; set; } = 0.5;
    public int MainWindowDockWorkAreaX { get; set; }
    public int MainWindowDockWorkAreaY { get; set; }
    public int MainWindowDockWorkAreaWidth { get; set; }
    public int MainWindowDockWorkAreaHeight { get; set; }
    public bool MainWindowHasExpandedBounds { get; set; }
    public int MainWindowExpandedX { get; set; }
    public int MainWindowExpandedY { get; set; }
    public double MainWindowExpandedWidth { get; set; } = 420;
    public double MainWindowExpandedHeight { get; set; } = 680;
    public bool MainWindowTopmost { get; set; } = true;

    /// <summary>便签贴边状态（按 memo id 共享），存 settings.json 而非 memos.json。</summary>
    public PopoutDockSettings PopoutDock { get; set; } = new();

    public static AppSettings CreateDefault() => new();

    /// <summary>
    /// Copies every persisted and runtime setting into <paramref name="target"/>.
    /// Keeping this operation beside the model makes settings preview/reset paths
    /// auditable: adding a field requires updating both Clone and CopyTo.
    /// </summary>
    public void CopyTo(AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        CopyUserPreferencesTo(target);
        CopyMainWindowRuntimeStateTo(target);
    }

    /// <summary>Copies settings exposed as user preferences by the settings window.</summary>
    public void CopyUserPreferencesTo(AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Language = Language;
        target.ThemeMode = ThemeMode;
        target.MotionMode = MotionMode;
        target.CloseButtonAction = CloseButtonAction;
        target.HasAskedCloseButtonAction = HasAskedCloseButtonAction;
        target.ToggleTopmostHotkey = ToggleTopmostHotkey.Clone();
        target.ToggleMemoTaskbarHotkey = ToggleMemoTaskbarHotkey.Clone();
        target.MinimizeHotkey = MinimizeHotkey.Clone();
        target.ShowWindowHotkey = ShowWindowHotkey.Clone();
        target.QuickMemoHotkey = QuickMemoHotkey.Clone();
        target.QuickMemoEnabled = QuickMemoEnabled;
        target.DuplicateMemoEnabled = DuplicateMemoEnabled;
        target.TraySingleClickToShow = TraySingleClickToShow;
        target.ShowMainWindowTaskbarIcon = ShowMainWindowTaskbarIcon;
        target.ShowMemoWindowTaskbarIcon = ShowMemoWindowTaskbarIcon;
        target.QuickMemoShowPopoutAfterAdd = QuickMemoShowPopoutAfterAdd;
        target.MemoWindowTopmostByDefault = MemoWindowTopmostByDefault;
        target.ReminderNotification = ReminderNotification;
        target.PopoutDockEnabled = PopoutDockEnabled;
        target.MainWindowDockEnabled = MainWindowDockEnabled;
        target.MainWindowDockSize = MainWindowDockSize;
    }

    /// <summary>
    /// Copies geometry, docking position and pin state owned by the live main
    /// window. Resetting preferences deliberately preserves these fields.
    /// </summary>
    public void CopyMainWindowRuntimeStateTo(AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.MainWindowDocked = MainWindowDocked;
        target.MainWindowDockEdge = MainWindowDockEdge;
        target.MainWindowDockPosition = MainWindowDockPosition;
        target.MainWindowDockWorkAreaX = MainWindowDockWorkAreaX;
        target.MainWindowDockWorkAreaY = MainWindowDockWorkAreaY;
        target.MainWindowDockWorkAreaWidth = MainWindowDockWorkAreaWidth;
        target.MainWindowDockWorkAreaHeight = MainWindowDockWorkAreaHeight;
        target.MainWindowHasExpandedBounds = MainWindowHasExpandedBounds;
        target.MainWindowExpandedX = MainWindowExpandedX;
        target.MainWindowExpandedY = MainWindowExpandedY;
        target.MainWindowExpandedWidth = MainWindowExpandedWidth;
        target.MainWindowExpandedHeight = MainWindowExpandedHeight;
        target.MainWindowTopmost = MainWindowTopmost;
        target.PopoutDock = PopoutDock.Clone();
    }

    public AppSettings Clone() => new()
    {
        Language = Language,
        ThemeMode = ThemeMode,
        MotionMode = MotionMode,
        CloseButtonAction = CloseButtonAction,
        HasAskedCloseButtonAction = HasAskedCloseButtonAction,
        ToggleTopmostHotkey = ToggleTopmostHotkey.Clone(),
        ToggleMemoTaskbarHotkey = ToggleMemoTaskbarHotkey.Clone(),
        MinimizeHotkey = MinimizeHotkey.Clone(),
        ShowWindowHotkey = ShowWindowHotkey.Clone(),
        QuickMemoHotkey = QuickMemoHotkey.Clone(),
        QuickMemoEnabled = QuickMemoEnabled,
        DuplicateMemoEnabled = DuplicateMemoEnabled,
        TraySingleClickToShow = TraySingleClickToShow,
        ShowMainWindowTaskbarIcon = ShowMainWindowTaskbarIcon,
        ShowMemoWindowTaskbarIcon = ShowMemoWindowTaskbarIcon,
        QuickMemoShowPopoutAfterAdd = QuickMemoShowPopoutAfterAdd,
        MemoWindowTopmostByDefault = MemoWindowTopmostByDefault,
        ReminderNotification = ReminderNotification,
        PopoutDockEnabled = PopoutDockEnabled,
        MainWindowDockEnabled = MainWindowDockEnabled,
        MainWindowDocked = MainWindowDocked,
        MainWindowDockSize = MainWindowDockSize,
        MainWindowDockEdge = MainWindowDockEdge,
        MainWindowDockPosition = MainWindowDockPosition,
        MainWindowDockWorkAreaX = MainWindowDockWorkAreaX,
        MainWindowDockWorkAreaY = MainWindowDockWorkAreaY,
        MainWindowDockWorkAreaWidth = MainWindowDockWorkAreaWidth,
        MainWindowDockWorkAreaHeight = MainWindowDockWorkAreaHeight,
        MainWindowHasExpandedBounds = MainWindowHasExpandedBounds,
        MainWindowExpandedX = MainWindowExpandedX,
        MainWindowExpandedY = MainWindowExpandedY,
        MainWindowExpandedWidth = MainWindowExpandedWidth,
        MainWindowExpandedHeight = MainWindowExpandedHeight,
        MainWindowTopmost = MainWindowTopmost,
        PopoutDock = PopoutDock.Clone(),
    };
}

/// <summary>便签贴边的按 memo 共享 UI 状态。键为 MemoItem.Id 的字符串形式。</summary>
public sealed class PopoutDockSettings
{
    public const double DefaultPopLength = 100;
    public const double MinPopLength = 40;
    public const double MaxPopLength = 400;

    /// <summary>每个备忘录的贴边弹出长度（DIP），缺省使用内置默认值。</summary>
    public Dictionary<string, double> PopLengths { get; set; } = new(StringComparer.Ordinal);

    public PopoutDockSettings Clone() => new()
    {
        PopLengths = new Dictionary<string, double>(PopLengths, StringComparer.Ordinal),
    };

    public static double ClampPopLength(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, MinPopLength, MaxPopLength) : DefaultPopLength;

    /// <summary>读取弹出长度；无记录或越界时回退到 clamp 后的内置默认值。</summary>
    public double GetPopLength(Guid memoId) =>
        PopLengths.TryGetValue(memoId.ToString(), out double length) ? ClampPopLength(length) : DefaultPopLength;
}

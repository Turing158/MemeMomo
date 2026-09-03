namespace Memo.Models;

/// <summary>提醒到期时的投递方式。</summary>
public enum ReminderNotificationMode
{
    /// <summary>只发 Windows 系统通知，点击通知打开对应便签（遵循系统免打扰设置）。</summary>
    SystemToast,
    /// <summary>只在屏幕中央弹出应用内便签窗口。</summary>
    InAppWindow,
    /// <summary>系统通知与应用内窗口都执行。</summary>
    Both,
}

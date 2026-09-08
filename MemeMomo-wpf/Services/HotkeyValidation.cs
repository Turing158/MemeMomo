using System.Windows.Input;
using MemeMomo.Models;

namespace MemeMomo.Services;

public enum HotkeyAction
{
    ToggleTopmost,
    ToggleMemoTaskbar,
    Minimize,
    ShowWindow,
    QuickMemo,
}

public readonly record struct HotkeyValidationResult(bool IsValid, string Error, HotkeyAction? Conflict)
{
    public static HotkeyValidationResult Valid { get; } = new(true, string.Empty, null);
}

public static class HotkeyValidation
{
    public static HotkeyValidationResult Validate(HotkeySetting candidate, AppSettings settings, HotkeyAction current)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(settings);
        int modifierCount = (candidate.Ctrl ? 1 : 0)
            + (candidate.Alt ? 1 : 0)
            + (candidate.Shift ? 1 : 0)
            + (candidate.Win ? 1 : 0);
        if (candidate.IsEmpty || modifierCount == 0)
        {
            return new(false, LocalizationService.Get("快捷键不能只设置单个按键，请使用 Ctrl、Alt 或 Shift 加一个主键的两键及以上组合。"), null);
        }

        if (IsSystemReserved(candidate))
        {
            return new(false, LocalizationService.Get("该组合是系统快捷键或容易被 Windows 保留，不能设置为应用快捷键。"), null);
        }

        foreach ((HotkeyAction action, HotkeySetting hotkey) in EnabledHotkeys(settings))
        {
            if (action != current && Equals(candidate, hotkey))
            {
                return new(false, LocalizationService.Format("该快捷键已被「{0}」功能占用，请选择其他组合。", ActionName(action)), action);
            }
        }

        return HotkeyValidationResult.Valid;
    }

    public static bool FindFirstDuplicatePair(AppSettings settings, out string fieldA, out string fieldB)
    {
        (HotkeyAction Action, HotkeySetting Hotkey)[] present = EnabledHotkeys(settings)
            .Where(item => !item.Hotkey.IsEmpty)
            .ToArray();
        for (int left = 0; left < present.Length; left++)
        {
            for (int right = left + 1; right < present.Length; right++)
            {
                if (Equals(present[left].Hotkey, present[right].Hotkey))
                {
                    fieldA = ActionName(present[left].Action);
                    fieldB = ActionName(present[right].Action);
                    return true;
                }
            }
        }

        fieldA = fieldB = string.Empty;
        return false;
    }

    public static string? NormalizeKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => key.ToString()[1..],
        >= Key.NumPad0 and <= Key.NumPad9 => key.ToString(),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.LeftCtrl or Key.RightCtrl => "Ctrl",
        Key.LeftAlt or Key.RightAlt => "Alt",
        Key.LeftShift or Key.RightShift => "Shift",
        Key.LWin or Key.RWin => "Win",
        Key.Tab => "Tab",
        Key.CapsLock => "CapsLock",
        Key.Space => "Space",
        Key.OemTilde => "`",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe or Key.OemBackslash => "\\",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.Add => "NumPad+",
        Key.Subtract => "NumPad-",
        Key.Multiply => "NumPad*",
        Key.Divide => "NumPad/",
        Key.Decimal => "NumPad.",
        Key.Insert => "Insert",
        Key.Delete => "Delete",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Escape => "Esc",
        _ => null,
    };

    public static string ActionName(HotkeyAction action) => LocalizationService.Get(action switch
    {
        HotkeyAction.ToggleTopmost => "置顶",
        HotkeyAction.ToggleMemoTaskbar => "切换便签任务栏图标",
        HotkeyAction.Minimize => "最小化",
        HotkeyAction.ShowWindow => "显示软件",
        HotkeyAction.QuickMemo => "快速添加（剪贴板）",
        _ => "其他功能",
    });

    public static bool IsModifierKey(string key) => key is "Ctrl" or "Alt" or "Shift" or "Win";

    public static bool Equals(HotkeySetting left, HotkeySetting right) =>
        left.Ctrl == right.Ctrl
        && left.Alt == right.Alt
        && left.Shift == right.Shift
        && left.Win == right.Win
        && string.Equals(left.Key, right.Key, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(HotkeyAction Action, HotkeySetting Hotkey)> EnabledHotkeys(AppSettings settings)
    {
        yield return (HotkeyAction.ToggleTopmost, settings.ToggleTopmostHotkey);
        yield return (HotkeyAction.Minimize, settings.MinimizeHotkey);
        yield return (HotkeyAction.ShowWindow, settings.ShowWindowHotkey);
        if (settings.ShowMemoWindowTaskbarIcon)
        {
            yield return (HotkeyAction.ToggleMemoTaskbar, settings.ToggleMemoTaskbarHotkey);
        }
        if (settings.QuickMemoEnabled)
        {
            yield return (HotkeyAction.QuickMemo, settings.QuickMemoHotkey);
        }
    }

    private static bool IsSystemReserved(HotkeySetting hotkey)
    {
        string key = hotkey.Key;
        if (hotkey.Win) return true;
        if (key is "Ctrl" or "Alt" or "Shift" or "Win") return true;
        if (hotkey.Ctrl && hotkey.Alt && key == "Delete") return true;
        if (hotkey.Alt && key is "Tab" or "F4" or "Space" or "Esc") return true;
        if (hotkey.Ctrl && key is "Esc" or "Tab") return true;
        if (hotkey.Ctrl && hotkey.Shift && key == "Esc") return true;
        if (hotkey.Ctrl && key is "C" or "V" or "X" or "Z" or "Y" or "A" or "S" or "P" or "F" or "N" or "O" or "W") return true;
        return false;
    }
}

using System.Windows.Media;
using MemeMomo.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace MemeMomo.UI;

internal static class TaskStatusAppearance
{
    internal static string IconLabel(string? key) => LocalizationService.Get(key switch
    {
        "Play" => "播放",
        "Pause" => "暂停",
        "Check" => "勾选",
        "Cancel" => "叉号",
        "Star" => "星标",
        "Flag" => "旗帜",
        "Clock" => "时钟",
        _ => "圆环"
    });
    internal static Brush Foreground(string? key)
    {
        string value = (key, ThemePreferences.IsDark) switch
        {
            ("Green", false) => "#216846",
            ("Green", true) => "#91D5AC",
            ("Amber", false) => "#78540C",
            ("Amber", true) => "#E8C176",
            ("Purple", false) => "#684797",
            ("Purple", true) => "#CDB0F1",
            ("Red", false) => "#A23434",
            ("Red", true) => "#F4A4A4",
            ("Gray", false) => "#595D65",
            ("Gray", true) => "#BFC5CE",
            (_, false) => "#2B5F91",
            (_, true) => "#9AC8F2"
        };
        return new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(value));
    }
    internal static Brush Background(string? key)
    {
        var color = ((SolidColorBrush)Foreground(key)).Color;
        color.A = ThemePreferences.IsDark ? (byte)30 : (byte)20;
        return new SolidColorBrush(color);
    }
}

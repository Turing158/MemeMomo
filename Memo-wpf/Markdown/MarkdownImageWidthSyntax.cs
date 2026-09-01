using System.Globalization;
using System.Text.RegularExpressions;

namespace Memo.Markdown;

/// <summary>
/// 图片宽度百分比后缀的解析与生成：在 "![名称](链接)" 之后追加 ",25%" 即把图片固定为
/// 编辑框宽度的 25%。百分比必须是大于 0 的数字；缺失、格式错误或非法值都按自适应处理。
/// </summary>
internal static partial class MarkdownImageWidthSyntax
{
    public static double? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent) ||
            !double.IsFinite(percent) ||
            percent <= 0)
        {
            return null;
        }
        return percent;
    }

    public static string Format(double percent) => percent.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>去掉图片源码末尾已有的宽度后缀（例如 ",25%"），返回不含后缀的图片源码。</summary>
    public static string StripSuffix(string source) => SuffixSyntax().Replace(source, string.Empty);

    public static string WithPercent(string source, double percent) =>
        $"{StripSuffix(source)},{Format(percent)}%";

    [GeneratedRegex(@",\s*\d+(?:\.\d+)?\s*%\s*$")] private static partial Regex SuffixSyntax();
}

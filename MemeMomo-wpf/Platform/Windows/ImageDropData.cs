using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using MemeMomo.Services;
using DataFormats = System.Windows.DataFormats;
using IDataObject = System.Windows.IDataObject;

namespace MemeMomo.Platform.Windows;

/// <summary>Reads browser image data without treating its textual representation as editor text.</summary>
internal sealed partial record ImageDropData(string Value)
{
    internal static ImageDropData? Read(IDataObject data)
    {
        foreach (string format in new[] { DataFormats.UnicodeText, DataFormats.Text })
        {
            if (data.GetDataPresent(format) && data.GetData(format) is string text && IsImageUri(text.Trim()))
                return new(text.Trim());
        }
        if (data.GetDataPresent(DataFormats.Html) && data.GetData(DataFormats.Html) is string html)
        {
            Match match = HtmlImageSource().Match(html);
            if (match.Success)
                return new(WebUtility.HtmlDecode(match.Groups["uri"].Value).Trim());
        }
        return null;
    }

    internal bool TryGetFormat(out string extension, out int payloadStart, out string? error)
    {
        extension = string.Empty;
        payloadStart = Value.IndexOf(',') + 1;
        error = "不支持该图片格式。";
        if (payloadStart == 0) return false;
        string header = Value[..(payloadStart - 1)];
        if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return false;
        extension = header.Split(';')[0].ToLowerInvariant() switch
        {
            "data:image/png" or "data:image/x-png" => ".png",
            "data:image/jpeg" or "data:image/jpg" => ".jpg",
            "data:image/gif" => ".gif",
            "data:image/webp" => ".webp",
            "data:image/bmp" or "data:image/x-ms-bmp" => ".bmp",
            "data:image/svg+xml" => ".svg",
            _ => string.Empty
        };
        if (extension.Length == 0 || payloadStart == Value.Length) return false;
        if (Value.Length - payloadStart > (MarkdownImageStore.MaximumImageBytes + 2) / 3 * 4)
        {
            error = "图片不能超过 20 MB。";
            return false;
        }
        error = null;
        return true;
    }

    internal (byte[] Bytes, string Extension) Decode()
    {
        if (!TryGetFormat(out string extension, out int payloadStart, out string? error))
            throw new InvalidDataException(LocalizationService.Get(error!));
        byte[] bytes;
        try { bytes = Convert.FromBase64String(Value[payloadStart..]); }
        catch (FormatException exception)
        {
            throw new InvalidDataException(LocalizationService.Get("不支持该图片格式。"), exception);
        }
        if (bytes.Length == 0)
            throw new InvalidDataException(LocalizationService.Get("不支持该图片格式。"));
        if (bytes.LongLength > MarkdownImageStore.MaximumImageBytes)
            throw new InvalidDataException(LocalizationService.Get("图片不能超过 20 MB。"));
        return (bytes, extension);
    }

    private static bool IsImageUri(string value) => value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("""<img\b[^>]*?\ssrc\s*=\s*(?:"(?<uri>data:image/[^\"]*)"|'(?<uri>data:image/[^']*)'|(?<uri>data:image/[^\s>]+))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex HtmlImageSource();
}

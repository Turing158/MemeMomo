using System.Net;
using System.Text.RegularExpressions;

namespace MemeMomo.Markdown;

/// <summary>Edits image values without rewriting surrounding syntax or unrelated attributes.</summary>
internal sealed partial class ImageSourceSyntax
{
    private readonly int _altStart;
    private readonly int _altLength;
    private readonly int _pathStart;
    private readonly int _pathLength;
    private readonly Match[] _attributes;

    private ImageSourceSyntax(string source, bool isHtml, string alt, string path,
        int altStart = 0, int altLength = 0, int pathStart = 0, int pathLength = 0, Match[]? attributes = null)
    {
        Source = source;
        IsHtml = isHtml;
        Alt = alt;
        Path = path;
        _altStart = altStart;
        _altLength = altLength;
        _pathStart = pathStart;
        _pathLength = pathLength;
        _attributes = attributes ?? [];
    }

    internal string Source { get; }
    internal bool IsHtml { get; }
    internal string Alt { get; }
    internal string Path { get; }
    internal string? Title { get; private init; }
    internal double? WidthPercent { get; private init; }

    internal static bool TryParse(string source, out ImageSourceSyntax image)
    {
        image = null!;
        if (source.StartsWith("![", StringComparison.Ordinal))
            return TryReadMarkdown(source, 0, out image) && image.Source.Length == source.Length;
        Match tag = HtmlImage().Match(source);
        if (!tag.Success) return false;
        Group body = tag.Groups["body"];
        var attributes = new List<Match>();
        int cursor = body.Index;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (cursor < body.Index + body.Length)
        {
            Match attribute = HtmlAttribute().Match(source, cursor, body.Index + body.Length - cursor);
            if (!attribute.Success) break;
            if (attribute.Index != cursor ||
                !values.TryAdd(attribute.Groups["name"].Value, DecodeAttribute(attribute))) return false;
            attributes.Add(attribute);
            cursor += attribute.Length;
        }
        if (!string.IsNullOrWhiteSpace(source[cursor..(body.Index + body.Length)])) return false;
        image = new ImageSourceSyntax(source, true, values.GetValueOrDefault("alt", ""),
            values.GetValueOrDefault("src", ""), attributes: attributes.ToArray());
        return true;
    }

    internal static IEnumerable<(int Start, ImageSourceSyntax Image)> FindMarkdownImages(string text)
    {
        int start = 0;
        while ((start = text.IndexOf("![", start, StringComparison.Ordinal)) >= 0)
        {
            if (TryReadMarkdown(text, start, out var image))
            {
                yield return (start, image);
                start += image.Source.Length;
            }
            else start += 2;
        }
    }

    private static bool TryReadMarkdown(string text, int start, out ImageSourceSyntax image)
    {
        image = null!;
        int altEnd = start + 2;
        int brackets = 0;
        for (; altEnd < text.Length; altEnd++)
        {
            char c = text[altEnd];
            if (c is '\r' or '\n') return false;
            if (c == '\\' && altEnd + 1 < text.Length) { altEnd++; continue; }
            if (c == '[') brackets++;
            if (c == ']' && brackets-- == 0) break;
        }
        if (altEnd + 1 >= text.Length || text[altEnd + 1] != '(') return false;
        int contentStart = altEnd + 2;
        int end = contentStart;
        int depth = 0;
        char quote = '\0';
        bool angle = false;
        for (; end < text.Length; end++)
        {
            char c = text[end];
            if (c is '\r' or '\n') return false;
            if (c == '\\' && end + 1 < text.Length) { end++; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (angle) { if (c == '>') angle = false; continue; }
            if (c == '<') { angle = true; continue; }
            if ((c is '\'' or '"') && end > contentStart && char.IsWhiteSpace(text[end - 1])) { quote = c; continue; }
            if (c == '(') depth++;
            if (c == ')' && depth-- == 0) break;
        }
        if (end == text.Length || quote != '\0' || angle) return false;
        int pathStart = contentStart;
        while (pathStart < end && char.IsWhiteSpace(text[pathStart])) pathStart++;
        int pathEnd = end;
        while (pathEnd > pathStart && char.IsWhiteSpace(text[pathEnd - 1])) pathEnd--;
        Match title = MarkdownTitle().Match(text[contentStart..end]);
        if (title.Success) pathEnd = contentStart + title.Index;
        if (pathEnd < pathStart) pathStart = pathEnd;
        while (pathEnd > pathStart && char.IsWhiteSpace(text[pathEnd - 1])) pathEnd--;
        string path = text[pathStart..pathEnd];
        if (path.StartsWith('<'))
        {
            if (!path.EndsWith('>')) return false;
            path = path[1..^1];
        }
        int length = end + 1 - start;
        Match width = WidthSuffix().Match(text, end + 1);
        if (width.Success) length += width.Length;
        image = new ImageSourceSyntax(text.Substring(start, length), false,
            DecodeMarkdown(text[(start + 2)..altEnd]), DecodeMarkdown(path),
            2, altEnd - start - 2, pathStart - start, pathEnd - pathStart)
        {
            Title = title.Success ? DecodeMarkdown(title.Groups["title"].Value) : null,
            WidthPercent = MarkdownImageWidthSyntax.Parse(width.Groups[1].Value)
        };
        return true;
    }

    internal string WithAlt(string value) => IsHtml ? WithAttribute("alt", value) :
        Replace(_altStart, _altLength, EncodeMarkdown(value).Replace("[", "\\[").Replace("]", "\\]"));

    internal string WithPath(string value)
    {
        if (IsHtml) return WithAttribute("src", value);
        string encoded = EncodeMarkdown(value).Replace("<", "\\<").Replace(">", "\\>")
            .Replace("(", "\\(").Replace(")", "\\)");
        bool angle = Source.AsSpan(_pathStart, _pathLength).StartsWith("<") || value.Any(char.IsWhiteSpace);
        return Replace(_pathStart, _pathLength, angle ? $"<{encoded}>" : encoded);
    }

    private string WithAttribute(string name, string value)
    {
        Match? attribute = _attributes.FirstOrDefault(item => item.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (attribute is null)
        {
            if (value.Length == 0) return Source;
            int insert = Source.Length - 1;
            if (Source[insert - 1] == '/') insert--;
            while (insert > 4 && char.IsWhiteSpace(Source[insert - 1])) insert--;
            return Replace(insert, 0, $" {name}=\"{WebUtility.HtmlEncode(value)}\"");
        }
        if (DecodeAttribute(attribute) == value) return Source;
        Group token = attribute.Groups["token"];
        string encoded = WebUtility.HtmlEncode(value);
        if (!token.Success)
        {
            int insert = attribute.Index + attribute.Length;
            return Replace(insert, 0, $"=\"{encoded}\"");
        }
        char quote = token.Value[0];
        if (quote is not ('\'' or '"')) quote = '"';
        return Replace(token.Index, token.Length, $"{quote}{encoded}{quote}");
    }

    private string Replace(int start, int length, string value) => Source[..start] + value + Source[(start + length)..];
    private static string DecodeAttribute(Match attribute) => WebUtility.HtmlDecode(attribute.Groups["value"].Value);
    private static string DecodeMarkdown(string value) => WebUtility.HtmlDecode(MarkdownEscape().Replace(value, "$1"));
    private static string EncodeMarkdown(string value) => value.Replace("&", "&amp;").Replace("\\", "\\\\")
        .Replace("\r", "&#13;").Replace("\n", "&#10;");

    // The outer tag is protected while an attribute assignment is still being typed.
    internal static bool HasHtmlEnvelope(string source)
    {
        if (source.Length < 5 || !source.StartsWith("<img", StringComparison.OrdinalIgnoreCase) ||
            !source.EndsWith('>') || (!char.IsWhiteSpace(source[4]) && source[4] is not ('/' or '>'))) return false;
        char quote = '\0';
        for (int index = 4; index < source.Length - 1; index++)
        {
            char c = source[index];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '\'' or '"') quote = c;
            else if (c is '<' or '>') return false;
        }
        return true;
    }

    [GeneratedRegex("\\A<img(?=\\s|/?>)(?<body>(?:[^<>\"']|\"[^\"]*\"|'[^']*')*?)/?>\\z", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlImage();
    [GeneratedRegex("\\G\\s+(?<name>[A-Za-z_:][\\w:.-]*)(?:\\s*=\\s*(?<token>\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s'\"=<>`]+)))?")]
    private static partial Regex HtmlAttribute();
    [GeneratedRegex("\\s+(?:\"(?<title>(?:\\\\.|[^\"\\\\])*)\"|'(?<title>(?:\\\\.|[^'\\\\])*)'|\\((?<title>(?:\\\\.|[^)\\\\])*)\\))\\s*\\z")]
    private static partial Regex MarkdownTitle();
    [GeneratedRegex(@"\G,\s*(\d+(?:\.\d+)?)\s*%")]
    private static partial Regex WidthSuffix();
    [GeneratedRegex(@"\\([!""#$%&'()*+,\-./:;<=>?@\[\]\\^_`{|}~])")]
    private static partial Regex MarkdownEscape();
}

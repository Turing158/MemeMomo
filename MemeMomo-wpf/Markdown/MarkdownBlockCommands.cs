using System.Net;
using System.Text.RegularExpressions;
using Markdig.Syntax;

namespace MemeMomo.Markdown;

internal static partial class MarkdownBlockCommands
{
    internal static bool TryNormalizeLanguage(string? value, out string language)
    {
        language = value?.Trim() ?? string.Empty;
        return language.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '+' or '#' or '.');
    }

    internal static MarkdownEditResult Delete(MarkdownDocumentModel model, MarkdownVisualSpan span)
    {
        string source = model.Markdown;
        int start = IncludeIndentation(source, span.SourceStart);
        int end = span.SourceStart + span.SourceLength;
        int before = 0;
        while (before < 2 && start - before > 0 && source[start - before - 1] == '\n') before++;
        int after = 0;
        while (after < 2 && end + after < source.Length && source[end + after] == '\n') after++;
        int remove = Math.Max(0, before + after - (start == before || end + after == source.Length ? 0 : 2));
        int eatBefore = Math.Min(before, remove);
        start -= eatBefore;
        end += remove - eatBefore;
        return new MarkdownEditResult(source.Remove(start, end - start), start, start);
    }

    internal static string WithLanguage(MarkdownDocumentModel model, MarkdownVisualSpan span, string language)
    {
        if (!TryNormalizeLanguage(language, out language)) throw new ArgumentException("Invalid code language.", nameof(language));
        string source = model.Markdown;
        string block = source.Substring(span.SourceStart, span.SourceLength);
        Match fence = OpeningFence().Match(block);
        if (fence.Success)
        {
            int labelStart = span.SourceStart + fence.Length;
            int lineEnd = source.IndexOf('\n', labelStart);
            if (lineEnd < 0) lineEnd = source.Length;
            return source[..labelStart] + language + source[lineEnd..];
        }
        Match html = HtmlOpeningTag().Match(block);
        if (html.Success)
        {
            Match? attribute = Attributes(html.Value).FirstOrDefault(match =>
                match.Groups["name"].Value.Equals("data-language", StringComparison.OrdinalIgnoreCase));
            string tag = html.Value;
            if (attribute is not null)
                tag = tag[..attribute.Index] + $" data-language=\"{WebUtility.HtmlEncode(language)}\"" + tag[(attribute.Index + attribute.Length)..];
            else
                tag = tag[..^1] + $" data-language=\"{WebUtility.HtmlEncode(language)}\">";
            return source[..span.SourceStart] + tag + source[(span.SourceStart + html.Length)..];
        }
        // Indented code has no language field. Keep its logical code and wrap it in a safe fence.
        CodeBlock? indented = model.Ast.OfType<CodeBlock>().FirstOrDefault(value => value.Span.Start == span.SourceStart);
        if (indented is null || language.Length == 0) return source;
        string content = indented.Lines.ToString();
        int fenceLength = Math.Max(3, Backticks().Matches(content).Select(match => match.Length + 1).DefaultIfEmpty(3).Max());
        string marker = new('`', fenceLength);
        string replacement = marker + language + "\n" + content + "\n" + marker;
        int start = IncludeIndentation(source, span.SourceStart);
        return source[..start] + replacement + source[(span.SourceStart + span.SourceLength)..];
    }

    internal static string? ReadHtmlLanguage(string openingTag)
    {
        Match[] attributes = Attributes(openingTag);
        Match? explicitLanguage = attributes.FirstOrDefault(match =>
            match.Groups["name"].Value.Equals("data-language", StringComparison.OrdinalIgnoreCase));
        if (explicitLanguage is not null) return AttributeValue(explicitLanguage);
        Match? classes = attributes.FirstOrDefault(match =>
            match.Groups["name"].Value.Equals("class", StringComparison.OrdinalIgnoreCase));
        return classes is null ? null : AttributeValue(classes).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.StartsWith("language-", StringComparison.OrdinalIgnoreCase))?[9..];
    }

    private static Match[] Attributes(string openingTag) => HtmlAttribute().Matches(openingTag).ToArray();
    private static string AttributeValue(Match attribute) => WebUtility.HtmlDecode(
        attribute.Groups["quoted"].Success ? attribute.Groups["quoted"].Value : attribute.Groups["bare"].Value);

    private static int IncludeIndentation(string source, int start)
    {
        int lineStart = start == 0 ? 0 : source.LastIndexOf('\n', start - 1) + 1;
        return source.AsSpan(lineStart, start - lineStart).Trim(" \t").IsEmpty ? lineStart : start;
    }

    [GeneratedRegex(@"\A[ ]{0,3}(?:`{3,}|~{3,})")]
    private static partial Regex OpeningFence();
    [GeneratedRegex(@"`+")]
    private static partial Regex Backticks();
    [GeneratedRegex("\\A<pre\\b(?:[^<>\"']|\"[^\"]*\"|'[^']*')*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlOpeningTag();
    [GeneratedRegex("\\s+(?<name>[A-Za-z_:][\\w:.-]*)(?:\\s*=\\s*(?:(?<quote>['\"])(?<quoted>.*?)\\k<quote>|(?<bare>[^\\s'\"=<>`]+)))?", RegexOptions.Singleline)]
    private static partial Regex HtmlAttribute();
}

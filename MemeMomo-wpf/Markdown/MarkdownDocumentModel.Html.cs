using System.Net;
using System.Text.RegularExpressions;

namespace MemeMomo.Markdown;

internal sealed partial class MarkdownDocumentModel
{
    private sealed record HtmlElement(string Tag, Match Open, Match Close, string? Target,
        Dictionary<string, string> Attributes)
    {
        public int ContentStart => Open.Index + Open.Length;
        public int ContentEnd => Close.Index;
    }

    private void AnalyzeSafeHtml(bool[] hidden, Dictionary<int, Replacement> replacements,
        List<SourceStyle> styles, IReadOnlyList<SourceRange> excludedRanges)
    {
        var stacks = new Dictionary<string, Stack<(Match Match, string? Target, Dictionary<string, string> Attributes)>>(StringComparer.OrdinalIgnoreCase);
        var elements = new List<HtmlElement>();
        var boundaries = new List<(Match Tag, string Prefix)>();
        var codeContents = HtmlCodeContentRanges();
        var literalRanges = CodeSyntax().Matches(Markdown)
            .Where(match => !Overlaps(match.Index, match.Index + match.Length, codeContents))
            .Select(match => new SourceRange(match.Index, match.Index + match.Length)).ToArray();
        Match[] tags = HtmlTagSyntax().Matches(Markdown).ToArray();
        foreach (Match match in tags)
        {
            if (Overlaps(match.Index, match.Index + match.Length, excludedRanges) ||
                literalRanges.Any(range => match.Index >= range.Start && match.Index < range.End) ||
                Overlaps(match.Index, match.Index + match.Length, codeContents)) continue;
            string tag = match.Groups[2].Value.ToLowerInvariant();
            bool closing = match.Groups[1].Success;
            if (!TryReadSafeHtmlTag(tag, closing, match.Groups[3].Value, out var target, out var alt, out var attributes)) continue;
            HtmlPresentation presentation = HtmlPresentation.Parse(tag, attributes);
            if (tag is "br" or "hr" or "img")
            {
                replacements[match.Index] = tag switch
                {
                    "br" => new Replacement(match.Index + match.Length, "\n", MarkdownVisualKind.Normal),
                    "hr" => new Replacement(match.Index + match.Length, "                ", MarkdownVisualKind.Rule, Presentation: presentation),
                    _ => new Replacement(match.Index + match.Length, "\uFFFC", MarkdownVisualKind.Image,
                        ImageUri: target, AltText: alt, ImageWidthPercent: presentation.WidthPercent, Presentation: presentation)
                };
                continue;
            }
            if (!closing)
            {
                if (!stacks.TryGetValue(tag, out var stack)) stacks[tag] = stack = new();
                stack.Push((match, target, attributes));
                continue;
            }
            if (!stacks.TryGetValue(tag, out var opens) || opens.Count == 0) continue;
            var open = opens.Pop();
            elements.Add(new HtmlElement(tag, open.Match, match, open.Target, open.Attributes));
            Hide(hidden, open.Match.Index, open.Match.Index + open.Match.Length);
            Hide(hidden, match.Index, match.Index + match.Length);
        }

        // HTML allows an open container to continue to the end of the document.
        Match documentEnd = Regex.Match(Markdown, @"\z");
        foreach (var stack in stacks)
        {
            foreach (var open in stack.Value)
            {
                elements.Add(new HtmlElement(stack.Key, open.Match, documentEnd, open.Target, open.Attributes));
                Hide(hidden, open.Match.Index, open.Match.Index + open.Match.Length);
            }
        }

        foreach (HtmlElement element in elements)
        {
            int start = element.ContentStart;
            int end = element.ContentEnd;
            HtmlPresentation presentation = HtmlPresentation.Parse(element.Tag, element.Attributes);
            var kind = element.Tag switch
            {
                "b" or "strong" => MarkdownVisualKind.Bold,
                "i" or "em" => MarkdownVisualKind.Italic,
                "s" or "del" or "strike" => MarkdownVisualKind.Strike,
                "u" or "ins" => MarkdownVisualKind.Underline,
                "mark" => MarkdownVisualKind.Mark,
                "code" => MarkdownVisualKind.Code,
                "a" => MarkdownVisualKind.Link,
                "h1" => MarkdownVisualKind.Heading1,
                "h2" => MarkdownVisualKind.Heading2,
                "h3" => MarkdownVisualKind.Heading3,
                "h4" or "h5" or "h6" => MarkdownVisualKind.Heading4,
                "blockquote" => MarkdownVisualKind.Quote,
                "pre" => MarkdownVisualKind.CodeBlock,
                _ => MarkdownVisualKind.Normal
            };
            if (kind == MarkdownVisualKind.CodeBlock)
            {
                string? language = MarkdownBlockCommands.ReadHtmlLanguage(element.Open.Value);
                // A conventional <pre><code>...</code></pre> is one code block.
                Match wrapper = HtmlPreCodeWrapperSyntax().Match(Markdown[start..end]);
                if (wrapper.Success)
                {
                    language ??= MarkdownBlockCommands.ReadHtmlLanguage(HtmlTagSyntax().Match(Markdown, start).Value);
                    int contentStart = start + wrapper.Groups[1].Index;
                    int contentEnd = contentStart + wrapper.Groups[1].Length;
                    Hide(hidden, start, contentStart);
                    Hide(hidden, contentEnd, end);
                    start = contentStart;
                    end = contentEnd;
                }
                if (start < end && Markdown[start] == '\n') hidden[start++] = true;
                if (end > start && Markdown[end - 1] == '\n') hidden[--end] = true;
                styles.Add(new SourceStyle(start, end, kind, CodeLabel: language,
                    CodeContent: WebUtility.HtmlDecode(Markdown[start..end]),
                    CodeContentSourceStart: start, CodeContentSourceEnd: end, Presentation: presentation,
                    BlockSourceStart: element.Open.Index, BlockSourceEnd: element.Close.Index + element.Close.Length));
            }
            else
                styles.Add(new SourceStyle(start, end, kind, element.Target, Presentation: presentation,
                    BlockSourceStart: kind == MarkdownVisualKind.Quote ? element.Open.Index : null,
                    BlockSourceEnd: kind == MarkdownVisualKind.Quote ? element.Close.Index + element.Close.Length : null));

            if (IsHtmlBlockTag(element.Tag))
            {
                string prefix = string.Empty;
                if (element.Tag == "li")
                {
                    HtmlElement? list = elements.Where(value => value.Tag is "ol" or "ul" &&
                        value.ContentStart <= element.Open.Index && value.ContentEnd >= element.Close.Index)
                        .OrderBy(value => value.ContentEnd - value.ContentStart).FirstOrDefault();
                    int number = HtmlListItemNumber(element, list, elements);
                    int depth = elements.Count(value => value.Tag is "ol" or "ul" &&
                        value.ContentStart <= element.Open.Index && value.ContentEnd >= element.Close.Index);
                    string bullet = list?.Attributes.GetValueOrDefault("type")?.ToLowerInvariant() switch
                    { "circle" => "◦ ", "square" => "▪ ", _ => "• " };
                    prefix = new string(' ', Math.Max(0, depth - 1) * 2) +
                        (list?.Tag == "ol" ? HtmlListNumber(number, list.Attributes.GetValueOrDefault("type")) + ". " : bullet);
                }
                boundaries.Add((element.Open, prefix));
                boundaries.Add((element.Close, string.Empty));
            }
        }

        // Put adjacent HTML blocks on separate editable lines without inserting
        // extra blank lines when the source already contains line breaks.
        foreach (var boundary in boundaries.OrderBy(value => value.Tag.Index))
        {
            int offset = boundary.Tag.Index;
            char? before = PreviousVisibleCharacter(offset, hidden, replacements);
            int after = offset + boundary.Tag.Length;
            while (after < Markdown.Length && hidden[after] && !replacements.ContainsKey(after)) after++;
            bool needsBreak = before is not (null or '\n') &&
                (boundary.Prefix.Length > 0 || after < Markdown.Length && Markdown[after] != '\n');
            string text = (needsBreak ? "\n" : string.Empty) + boundary.Prefix;
            if (text.Length > 0)
                replacements[offset] = new Replacement(offset + boundary.Tag.Length, text, MarkdownVisualKind.Normal);
        }

        foreach (Match entity in HtmlEntitySyntax().Matches(Markdown))
        {
            if (hidden[entity.Index] || OverlapsReplacement(replacements, entity.Index) ||
                Overlaps(entity.Index, entity.Index + entity.Length, excludedRanges) ||
                Overlaps(entity.Index, entity.Index + entity.Length, literalRanges) ||
                tags.Any(tag => entity.Index >= tag.Index && entity.Index < tag.Index + tag.Length)) continue;
            string decoded = WebUtility.HtmlDecode(entity.Value);
            if (decoded != entity.Value)
                replacements[entity.Index] = new Replacement(entity.Index + entity.Length, decoded, MarkdownVisualKind.Normal);
        }
    }

    private char? PreviousVisibleCharacter(int offset, bool[] hidden, Dictionary<int, Replacement> replacements)
    {
        for (int index = offset - 1; index >= 0; index--)
        {
            var replacement = replacements.FirstOrDefault(value => value.Key <= index && value.Value.SourceEnd > index);
            if (replacement.Value.Text is { Length: > 0 } text) return text[^1];
            if (!hidden[index]) return Markdown[index];
        }
        return null;
    }

    private SourceRange[] HtmlCodeContentRanges()
    {
        Match[] literals = CodeSyntax().Matches(Markdown).ToArray();
        return HtmlCodeContainerSyntax().Matches(Markdown)
            .Where(match => !literals.Any(literal => match.Index >= literal.Index && match.Index < literal.Index + literal.Length))
            .Select(match => new SourceRange(match.Groups[2].Index, match.Groups[2].Index + match.Groups[2].Length)).ToArray();
    }

    private static bool IsHtmlBlockTag(string tag) => tag is
        "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "p" or "div" or "blockquote" or "pre" or "ul" or "ol" or "li" or
        "center" or "section" or "article" or "header" or "footer" or "main";

    private static string HtmlListNumber(int number, string? type)
    {
        if (type is "a" or "A" && number > 0)
        {
            string label = string.Empty;
            for (int value = number; value > 0; value = (value - 1) / 26)
                label = (char)((type == "a" ? 'a' : 'A') + (value - 1) % 26) + label;
            return label;
        }
        if (type is "i" or "I" && number is > 0 and < 4000)
        {
            string label = string.Empty;
            foreach (var part in new[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") })
                while (number >= part.Item1) { label += part.Item2; number -= part.Item1; }
            return type == "i" ? label.ToLowerInvariant() : label;
        }
        return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int HtmlListItemNumber(HtmlElement item, HtmlElement? list, List<HtmlElement> elements)
    {
        if (list is null) return 1;
        var children = elements.Where(value => value.Tag == "li" && value.Open.Index >= list.ContentStart &&
            value.Close.Index <= list.ContentEnd && !elements.Any(nested => nested.Tag is "ul" or "ol" &&
                nested.Open.Index > list.Open.Index && nested.ContentStart <= value.Open.Index && nested.ContentEnd >= value.Close.Index))
            .OrderBy(value => value.Open.Index).ToArray();
        int step = list.Attributes.ContainsKey("reversed") ? -1 : 1;
        int number = int.TryParse(list.Attributes.GetValueOrDefault("start"), out int start) ? start : step < 0 ? children.Length : 1;
        foreach (var child in children)
        {
            if (int.TryParse(child.Attributes.GetValueOrDefault("value"), out int value)) number = value;
            if (child == item) return number;
            number += step;
        }
        return number;
    }

    [GeneratedRegex(@"<(pre|code)\b[^<>]*>(.*?)</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlCodeContainerSyntax();
    [GeneratedRegex(@"\A\s*<code\b[^<>]*>(.*?)</code\s*>\s*\z", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlPreCodeWrapperSyntax();
    [GeneratedRegex(@"&(?:#[0-9]+|#x[0-9a-fA-F]+|[A-Za-z][A-Za-z0-9]+);")]
    private static partial Regex HtmlEntitySyntax();
}

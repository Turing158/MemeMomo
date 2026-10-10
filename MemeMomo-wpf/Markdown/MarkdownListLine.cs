using System.Text.RegularExpressions;

namespace MemeMomo.Markdown;

/// <summary>Source line coordinates with the enclosing quote kept outside list content.</summary>
internal readonly partial record struct MarkdownListLine(int Start, int End, int ContentStart, string QuotePrefix, string Text)
{
    public int QuoteDepth => QuotePrefix.Count(character => character == '>');

    public static MarkdownListLine Read(string source, int offset)
    {
        offset = Math.Clamp(offset, 0, source.Length);
        int start = offset == 0 ? 0 : source.LastIndexOf('\n', offset - 1) + 1;
        int end = source.IndexOf('\n', start);
        if (end < 0) end = source.Length;
        int prefixLength = QuotePrefixLength(source[start..end]);
        return new(start, end, start + prefixLength, source.Substring(start, prefixLength), source[(start + prefixLength)..end]);
    }

    public bool TryPrevious(string source, out MarkdownListLine previous)
    {
        bool hasPrevious = Start > 0;
        int depth = QuoteDepth;
        previous = hasPrevious ? Read(source, Start - 1) : default;
        return hasPrevious && previous.QuoteDepth == depth;
    }

    public static int QuotePrefixLength(string line) => QuotePrefixSyntax().Match(line).Length;

    [GeneratedRegex(@"^(?:[ \t]*>[ \t]?)+")]
    private static partial Regex QuotePrefixSyntax();
}

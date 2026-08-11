using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Memo.Markdown;

/// <summary>
/// 将 Markdown 转成适合 Windows 通知的紧凑纯文本。
/// 复杂块只保留类型占位，避免表格或代码在通知中失去结构。
/// </summary>
public static partial class MarkdownNotificationText {
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static string BuildBody(string? markdown, string? title, int maximumLength = 360) {
        if (maximumLength <= 0 || string.IsNullOrWhiteSpace(markdown)) return string.Empty;

        var lines = ExtractLines(markdown);
        var titleIndex = lines.FindIndex(line =>
            !string.IsNullOrWhiteSpace(title) &&
            string.Equals(line, title.Trim(), StringComparison.Ordinal));
        if (titleIndex >= 0) lines.RemoveAt(titleIndex);

        var result = string.Join(Environment.NewLine, lines);
        if (result.Length <= maximumLength) return result;
        return result[..Math.Max(0, maximumLength - 1)].TrimEnd() + "…";
    }

    internal static List<string> ExtractLines(string markdown) {
        var lines = new List<string>();
        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        foreach (var block in document) AppendBlock(block, lines);
        return lines;
    }

    private static void AppendBlock(Block block, List<string> lines) {
        if (IsTable(block)) {
            AddLine(lines, "[表格]");
            return;
        }

        if (block is CodeBlock) {
            AddLine(lines, "[代码]");
            return;
        }

        if (block is ThematicBreakBlock) {
            AddLine(lines, "[分隔线]");
            return;
        }

        if (block is HtmlBlock htmlBlock) {
            AddLine(lines, HtmlTag().Replace(htmlBlock.Lines.ToString() ?? string.Empty, " "));
            return;
        }

        if (block is LeafBlock { Inline: { } inline }) {
            AddLine(lines, ReadInline(inline));
            return;
        }

        if (block is ContainerBlock container) {
            foreach (var child in container) AppendBlock(child, lines);
        }
    }

    private static string ReadInline(ContainerInline container) {
        var builder = new StringBuilder();
        AppendInlineChildren(container, builder);
        return builder.ToString();
    }

    private static void AppendInlineChildren(ContainerInline container, StringBuilder builder) {
        for (var inline = container.FirstChild; inline != null; inline = inline.NextSibling) {
            switch (inline) {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case LinkInline { IsImage: true }:
                    builder.Append("[图片]");
                    break;
                case AutolinkInline autoLink:
                    builder.Append(autoLink.Url);
                    break;
                case HtmlInline html:
                    builder.Append(HtmlTag().Replace(html.Tag, " "));
                    break;
                case LineBreakInline:
                    builder.Append(' ');
                    break;
                case ContainerInline nested:
                    AppendInlineChildren(nested, builder);
                    break;
            }
        }
    }

    private static void AddLine(List<string> lines, string value) {
        var normalized = Whitespace().Replace(TaskPrefix().Replace(value, string.Empty), " ").Trim();
        if (normalized.Length > 0) lines.Add(normalized);
    }

    private static bool IsTable(Block block) =>
        block.GetType().Name.Contains("Table", StringComparison.Ordinal);

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"^\s*\[[ xX]\]\s*")]
    private static partial Regex TaskPrefix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

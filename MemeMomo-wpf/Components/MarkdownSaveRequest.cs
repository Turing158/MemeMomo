namespace MemeMomo.Components;

public readonly record struct MarkdownSaveRequest(
    string Markdown,
    bool CompleteEditing,
    bool IsNewMemo);

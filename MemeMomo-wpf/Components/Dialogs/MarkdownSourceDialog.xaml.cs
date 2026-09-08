using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Button = System.Windows.Controls.Button;

namespace MemeMomo.Components.Dialogs;

public partial class MarkdownSourceDialog : MemoDialogWindow
{
    public MarkdownSourceDialog() : this(string.Empty) { }

    public MarkdownSourceDialog(string markdown)
    {
        InitializeComponent();
        SourceBox.Text = markdown;
        UpdateDocumentStats();
        Loaded += OnDialogLoaded;
        Closed += OnDialogClosed;
    }

    public string? Result { get; private set; }
    internal Button ApplyButtonPart => ApplyButton;
    internal string DocumentStats => DocumentStatsText.Text;

    public string? ShowDialog(Window owner) => ShowModal(owner, () => Result);

    protected override void AssignCancelResult() => Result = null;

    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        SourceBox.Focus();
        SourceBox.CaretIndex = SourceBox.Text.Length;
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        Loaded -= OnDialogLoaded;
        Closed -= OnDialogClosed;
    }

    private void OnSourceTextChanged(object sender, TextChangedEventArgs e) => UpdateDocumentStats();

    private void OnApplyClick(object sender, RoutedEventArgs e) => CloseDialog(() => Result = SourceBox.Text ?? string.Empty);
    private void OnCancelClick(object sender, RoutedEventArgs e) => CloseDialog(() => Result = null);

    private void OnDialogPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseDialog(() => Result = null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            CloseDialog(() => Result = SourceBox.Text ?? string.Empty);
            e.Handled = true;
        }
    }

    private void UpdateDocumentStats()
    {
        string source = SourceBox.Text ?? string.Empty;
        int lines = source.Length == 0 ? 1 : source.Split('\n').Length;
        if (source.EndsWith('\n')) lines = Math.Max(1, lines);
        MemeMomo.UI.Text.LocalizeExtension.Set(DocumentStatsText, TextBlock.TextProperty, "{0:N0} 行 · {1:N0} 字符", lines, source.Length);
    }

}

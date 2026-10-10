using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MemeMomo.Markdown;
using MemeMomo.UI;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MemeMomo.Components.Dialogs;

public partial class CodeLanguageDialog : MemoDialogWindow
{
    public CodeLanguageDialog(string language)
    {
        InitializeComponent();
        LanguageBox.Text = language;
        UpdateValidation();
        Loaded += OnDialogLoaded;
    }

    public string? Result { get; private set; }
    public string? ShowDialog(Window owner) => ShowModal(owner, () => Result);
    protected override void AssignCancelResult() => Result = null;

    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnDialogLoaded;
        LanguageBox.Focus();
        LanguageBox.SelectAll();
    }

    private void OnLanguageChanged(object sender, TextChangedEventArgs e)
    {
        if (SubmitButton is not null) UpdateValidation();
    }

    private void UpdateValidation()
    {
        bool valid = MarkdownBlockCommands.TryNormalizeLanguage(LanguageBox.Text, out _);
        SubmitButton.IsEnabled = valid;
        if (valid) Feedback.Clear();
        else Feedback.Show(MemeMomo.Services.LocalizationService.Get("语言名称仅支持字母、数字及 _ - + # ."), MemeMomo.UI.Notifications.NotificationKind.Error);
        InteractionState.SetIsInvalid(LanguageInputRoot, !valid);
    }

    private void OnSubmitClick(object sender, RoutedEventArgs e) => Submit();
    private void OnCancelClick(object sender, RoutedEventArgs e) => CloseDialog(() => Result = null);

    private void OnDialogPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseDialog(() => Result = null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.OriginalSource is System.Windows.Controls.TextBox)
        {
            Submit();
            e.Handled = true;
        }
    }

    private void Submit()
    {
        if (MarkdownBlockCommands.TryNormalizeLanguage(LanguageBox.Text, out string language))
            CloseDialog(() => Result = language);
    }
}

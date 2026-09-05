using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using MemeMomo.Services;
using MemeMomo.UI;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace MemeMomo.Components.Dialogs;

public partial class ImageUrlDialog : MemoDialogWindow
{
    private bool _showValidationError;

    public ImageUrlDialog()
    {
        InitializeComponent();
        Loaded += OnDialogLoaded;
        Closed += OnDialogClosed;
        UpdateState();
    }

    public string? Result { get; private set; }

    public string? ShowDialog(Window owner) => ShowModal(owner, () => Result);

    protected override void AssignCancelResult() => Result = null;

    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        UrlBox.Focus();
        UrlBox.SelectAll();
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        Loaded -= OnDialogLoaded;
        Closed -= OnDialogClosed;
    }

    private void OnUrlTextChanged(object sender, TextChangedEventArgs e) => UpdateState();

    private void OnClearUrlClick(object sender, RoutedEventArgs e)
    {
        _showValidationError = false;
        UrlBox.Clear();
        UrlBox.Focus();
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
        else if (e.Key == Key.Enter && e.OriginalSource is TextBox)
        {
            Submit();
            e.Handled = true;
        }
    }

    private void Submit()
    {
        string? url = UrlBox.Text?.Trim();
        if (!MarkdownImageStore.IsSafeRemoteImageUri(url))
        {
            _showValidationError = true;
            UpdateState();
            UrlBox.Focus();
            return;
        }

        CloseDialog(() => Result = url);
    }

    private void UpdateState()
    {
        string? url = UrlBox.Text?.Trim();
        bool hasValue = !string.IsNullOrWhiteSpace(url);
        bool showError = _showValidationError && !MarkdownImageStore.IsSafeRemoteImageUri(url);
        InteractionState.SetIsInvalid(UrlInputRoot, showError);
        ClearUrlButton.Visibility = hasValue ? Visibility.Visible : Visibility.Collapsed;
        SubmitButton.IsEnabled = hasValue;
        HelperText.Opacity = showError ? 0 : 1;
        ErrorText.Opacity = showError ? 1 : 0;
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Memo.UI;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace Memo.Components.Dialogs;

public sealed record LinkEditValue(string Label, string Url);

public partial class LinkEditDialog : MemoDialogWindow
{
    private bool _isInitializing = true;
    private bool _labelWasEdited;
    private bool _urlWasEdited;

    public LinkEditDialog() : this(string.Empty, string.Empty) { }

    public LinkEditDialog(string label, string url)
    {
        InitializeComponent();
        LabelBox.Text = label;
        UrlBox.Text = string.IsNullOrWhiteSpace(url) ? string.Empty : url;
        bool editing = !string.IsNullOrWhiteSpace(UrlBox.Text);
        Title = editing ? "编辑链接" : "插入链接";
        DialogTitleText.Text = Title;
        SubmitButtonText.Text = editing ? "保存修改" : "插入链接";
        AutomationProperties.SetName(SubmitButton, SubmitButtonText.Text);
        _isInitializing = false;
        UpdateValidation(false);
        Loaded += OnDialogLoaded;
        Closed += OnDialogClosed;
    }

    public LinkEditValue? Result { get; private set; }

    public LinkEditValue? ShowDialog(Window owner) => ShowModal(owner, () => Result);

    protected override void AssignCancelResult() => Result = null;

    internal static bool TryCreateValue(string? label, string? url, out LinkEditValue? value, out string? labelError, out string? urlError)
    {
        string? normalizedLabel = label?.Trim();
        string? normalizedUrl = url?.Trim();
        labelError = string.IsNullOrWhiteSpace(normalizedLabel) ? "请输入链接文本" : null;
        urlError = ValidateUrl(normalizedUrl);
        if (labelError is not null || urlError is not null)
        {
            value = null;
            return false;
        }

        value = new LinkEditValue(normalizedLabel!, normalizedUrl!);
        return true;
    }

    private static string? ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || string.Equals(url, "https://", StringComparison.OrdinalIgnoreCase) || string.Equals(url, "http://", StringComparison.OrdinalIgnoreCase)) return "请输入链接地址";
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return "请输入完整的链接地址";
        bool web = (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(uri.Host);
        if (web) return null;
        if (uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase) && uri.OriginalString.Length > "mailto:".Length) return null;
        return "仅支持 HTTP、HTTPS 或邮件地址";
    }

    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        LabelBox.Focus();
        LabelBox.SelectAll();
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        Loaded -= OnDialogLoaded;
        Closed -= OnDialogClosed;
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (ReferenceEquals(sender, LabelBox)) _labelWasEdited = true;
        if (ReferenceEquals(sender, UrlBox)) _urlWasEdited = true;
        UpdateValidation(false);
    }

    private void OnClearUrlClick(object sender, RoutedEventArgs e)
    {
        _urlWasEdited = true;
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
        if (!TryCreateValue(LabelBox.Text, UrlBox.Text, out LinkEditValue? value, out string? labelError, out _))
        {
            _labelWasEdited = true;
            _urlWasEdited = true;
            UpdateValidation(true);
            if (labelError is not null) LabelBox.Focus(); else UrlBox.Focus();
            return;
        }

        CloseDialog(() => Result = value);
    }

    private void UpdateValidation(bool showAllErrors)
    {
        bool valid = TryCreateValue(LabelBox.Text, UrlBox.Text, out _, out string? labelError, out string? urlError);
        bool showLabel = labelError is not null && (showAllErrors || _labelWasEdited);
        bool showUrl = urlError is not null && (showAllErrors || _urlWasEdited);
        SetValidation(LabelInputRoot, LabelErrorText, labelError, showLabel);
        SetValidation(UrlInputRoot, UrlErrorText, urlError, showUrl);
        ClearUrlButton.Visibility = string.IsNullOrEmpty(UrlBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        SubmitButton.IsEnabled = valid;
    }

    private static void SetValidation(Border root, TextBlock errorText, string? error, bool show)
    {
        InteractionState.SetIsInvalid(root, show);
        errorText.Text = error ?? string.Empty;
        errorText.Opacity = show ? 1 : 0;
    }
}

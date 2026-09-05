using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MemeMomo.UI;
using MemeMomo.Utils;
using System;

namespace MemeMomo.Components.Dialogs;

public sealed record LinkEditValue(string Label, string Url);

public partial class LinkEditDialog : Window {
    private readonly WindowTransitionController _transition;
    private bool _isInitializing = true;
    private bool _labelWasEdited;
    private bool _urlWasEdited;
    private bool _isClosingAfterTransition;

    public LinkEditDialog() : this(string.Empty, string.Empty) { }

    public LinkEditDialog(string label, string url) {
        InitializeComponent();

        _transition = new WindowTransitionController(this, _dialogShell);
        _transition.PrepareOpen();

        _labelBox.Text = label;
        _urlBox.Text = string.IsNullOrWhiteSpace(url) ? string.Empty : url;

        var isEditing = !string.IsNullOrWhiteSpace(_urlBox.Text);
        Title = isEditing ? "编辑链接" : "插入链接";
        _titleText.Text = Title;
        _submitButtonText.Text = isEditing ? "保存修改" : "插入链接";
        _submitButton.SetValue(AutomationProperties.NameProperty, _submitButtonText.Text);

        _isInitializing = false;
        UpdateValidation(showAllErrors: false);

        Opened += OnOpened;
        Closed += (_, _) => _transition.Cancel();
    }

    internal static bool TryCreateValue(
        string? label,
        string? url,
        out LinkEditValue? value,
        out string? labelError,
        out string? urlError) {
        var normalizedLabel = label?.Trim();
        var normalizedUrl = url?.Trim();

        labelError = string.IsNullOrWhiteSpace(normalizedLabel) ? "请输入链接文本" : null;
        urlError = ValidateUrl(normalizedUrl);

        if (labelError != null || urlError != null) {
            value = null;
            return false;
        }

        value = new LinkEditValue(normalizedLabel!, normalizedUrl!);
        return true;
    }

    private static string? ValidateUrl(string? url) {
        if (string.IsNullOrWhiteSpace(url) ||
            string.Equals(url, "https://", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(url, "http://", StringComparison.OrdinalIgnoreCase))
            return "请输入链接地址";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "请输入完整的链接地址";

        var isWebLink = uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if (isWebLink && !string.IsNullOrWhiteSpace(uri.Host)) return null;

        if (uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase) &&
            uri.OriginalString.Length > "mailto:".Length)
            return null;

        return "仅支持 HTTP、HTTPS 或邮件地址";
    }

    private void OnOpened(object? sender, EventArgs e) {
        _transition.PlayOpen();
        Dispatcher.UIThread.Post(() => {
            _labelBox.Focus();
            _labelBox.SelectAll();
        });
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (TitleBarDragHelper.CanStartDrag(this, e)) BeginMoveDrag(e);
    }

    private void OnInputTextChanged(object? sender, TextChangedEventArgs e) {
        if (_isInitializing) return;

        if (ReferenceEquals(sender, _labelBox)) _labelWasEdited = true;
        if (ReferenceEquals(sender, _urlBox)) _urlWasEdited = true;
        UpdateValidation(showAllErrors: false);
    }

    private void OnClearUrl(object? sender, RoutedEventArgs e) {
        _urlWasEdited = true;
        _urlBox.Text = string.Empty;
        _urlBox.Focus();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Submit();

    private void Submit() {
        if (!TryCreateValue(
                _labelBox.Text,
                _urlBox.Text,
                out var value,
                out var labelError,
                out _)) {
            _labelWasEdited = true;
            _urlWasEdited = true;
            UpdateValidation(showAllErrors: true);
            if (labelError != null) _labelBox.Focus();
            else _urlBox.Focus();
            return;
        }

        CloseWithTransition(value);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => CloseWithTransition(null);

    private void OnDialogKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key == Key.Escape) {
            CloseWithTransition(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.Source is TextBox) {
            Submit();
            e.Handled = true;
        }
    }

    private void UpdateValidation(bool showAllErrors) {
        var isValid = TryCreateValue(
            _labelBox.Text,
            _urlBox.Text,
            out _,
            out var labelError,
            out var urlError);

        var showLabelError = labelError != null && (showAllErrors || _labelWasEdited);
        var showUrlError = urlError != null && (showAllErrors || _urlWasEdited);
        SetFieldValidation(_labelInputRoot, _labelErrorText, labelError, showLabelError);
        SetFieldValidation(_urlInputRoot, _urlErrorText, urlError, showUrlError);

        _clearUrlButton.IsVisible = !string.IsNullOrEmpty(_urlBox.Text);
        _submitButton.IsEnabled = isValid;
    }

    private static void SetFieldValidation(
        Border inputRoot,
        TextBlock errorText,
        string? error,
        bool showError) {
        inputRoot.Classes.Set("invalid", showError);
        errorText.Text = error ?? string.Empty;
        errorText.Opacity = showError ? 1 : 0;
    }

    private void CloseWithTransition(LinkEditValue? result) {
        if (_isClosingAfterTransition) return;
        _isClosingAfterTransition = true;
        _transition.CloseAfterTransition(() => Close(result));
    }
}

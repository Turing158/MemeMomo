using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MemeMomo.Services;
using MemeMomo.UI;
using MemeMomo.Utils;
using System;

namespace MemeMomo.Components.Dialogs;

public partial class ImageUrlDialog : Window {
    private readonly WindowTransitionController _transition;
    private bool _showValidationError;
    private bool _isClosingAfterTransition;

    public ImageUrlDialog() {
        InitializeComponent();

        _transition = new WindowTransitionController(this, _dialogShell);
        _transition.PrepareOpen();

        Opened += OnOpened;
        Closed += (_, _) => _transition.Cancel();
    }

    private void OnOpened(object? sender, EventArgs e) {
        _transition.PlayOpen();
        Dispatcher.UIThread.Post(() => _urlBox.Focus());
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (TitleBarDragHelper.CanStartDrag(this, e)) BeginMoveDrag(e);
    }

    private void OnUrlTextChanged(object? sender, TextChangedEventArgs e) => UpdateState();

    private void OnClearUrlClick(object? sender, RoutedEventArgs e) {
        _showValidationError = false;
        _urlBox.Clear();
        _urlBox.Focus();
    }

    private void OnInsertClick(object? sender, RoutedEventArgs e) => Submit();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => CloseWithTransition(null);

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

    private void Submit() {
        var url = _urlBox.Text?.Trim();
        if (!MarkdownImageStore.IsSafeRemoteImageUri(url)) {
            _showValidationError = true;
            UpdateState();
            _urlBox.Focus();
            return;
        }

        CloseWithTransition(url);
    }

    private void UpdateState() {
        var url = _urlBox.Text?.Trim();
        var hasValue = !string.IsNullOrWhiteSpace(url);
        var showError = _showValidationError && !MarkdownImageStore.IsSafeRemoteImageUri(url);

        _urlInputRoot.Classes.Set("invalid", showError);
        _clearUrlButton.IsVisible = hasValue;
        _submitButton.IsEnabled = hasValue;
        _helperContent.Opacity = showError ? 0 : 1;
        _errorText.Opacity = showError ? 1 : 0;
    }

    private void CloseWithTransition(string? result) {
        if (_isClosingAfterTransition) return;
        _isClosingAfterTransition = true;
        _transition.CloseAfterTransition(() => Close(result));
    }
}

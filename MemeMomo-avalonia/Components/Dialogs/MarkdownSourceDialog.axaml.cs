using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MemeMomo.UI;
using MemeMomo.Utils;
using System;

namespace MemeMomo.Components.Dialogs;

public partial class MarkdownSourceDialog : Window {
    private readonly WindowTransitionController _transition;
    private bool _isClosingAfterTransition;

    public MarkdownSourceDialog() : this(string.Empty) { }

    public MarkdownSourceDialog(string markdown) {
        InitializeComponent();

        _transition = new WindowTransitionController(this, _dialogShell);
        _transition.PrepareOpen();

        _sourceBox.Text = markdown;
        UpdateDocumentStats();

        Opened += OnOpened;
        Loaded += (_, _) => this.AssignResizeCursors();
        PropertyChanged += (_, e) => {
            if (e.Property == WindowStateProperty) UpdateWindowStateVisuals();
        };
        Closed += (_, _) => _transition.Cancel();
    }

    private void OnOpened(object? sender, EventArgs e) {
        UpdateWindowStateVisuals();
        _transition.PlayOpen();
        Dispatcher.UIThread.Post(() => _sourceBox.Focus());
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (WindowState != WindowState.Maximized && TitleBarDragHelper.CanStartDrag(this, e))
            BeginMoveDrag(e);
    }

    private void UpdateWindowStateVisuals() {
        var isMaximized = WindowState == WindowState.Maximized;
        _resizeHandles.IsVisible = !isMaximized;
        _dialogShell.CornerRadius = isMaximized ? new CornerRadius(0) : new CornerRadius(14);
        _dialogShell.BorderThickness = isMaximized ? new Thickness(0) : new Thickness(1);
    }

    private void OnResizeHandlePointerPressed(object? sender, PointerPressedEventArgs e) {
        if (WindowState == WindowState.Maximized ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            sender is not Border handle ||
            handle.Tag is not string tag)
            return;

        var edge = tag switch {
            "Top" => WindowEdge.North,
            "Bottom" => WindowEdge.South,
            "Left" => WindowEdge.West,
            "Right" => WindowEdge.East,
            "TopLeft" => WindowEdge.NorthWest,
            "TopRight" => WindowEdge.NorthEast,
            "BottomLeft" => WindowEdge.SouthWest,
            "BottomRight" => WindowEdge.SouthEast,
            _ => WindowEdge.North,
        };
        BeginResizeDrag(edge, e);
    }

    private void OnSourceTextChanged(object? sender, TextChangedEventArgs e) => UpdateDocumentStats();

    private void UpdateDocumentStats() {
        var source = _sourceBox.Text ?? string.Empty;
        var lineCount = 1;

        for (var index = 0; index < source.Length; index++) {
            if (source[index] == '\n' ||
                (source[index] == '\r' && (index + 1 >= source.Length || source[index + 1] != '\n')))
                lineCount++;
        }

        _documentStatsText.Text = $"{lineCount:N0} 行 · {source.Length:N0} 字符";
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) =>
        CloseWithTransition(_sourceBox.Text ?? string.Empty);

    private void OnCancel(object? sender, RoutedEventArgs e) => CloseWithTransition(null);

    private void OnDialogKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key == Key.Escape) {
            CloseWithTransition(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) {
            CloseWithTransition(_sourceBox.Text ?? string.Empty);
            e.Handled = true;
        }
    }

    private void CloseWithTransition(string? result) {
        if (_isClosingAfterTransition) return;
        _isClosingAfterTransition = true;
        _transition.CloseAfterTransition(() => Close(result));
    }
}

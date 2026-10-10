using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MemeMomo.Markdown;
using MemeMomo.UI;
using MemeMomo.UI.Text;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MemeMomo.Components.Dialogs;

public partial class ImageEditDialog : MemoDialogWindow
{
    private ImageSourceSyntax _image;
    private readonly bool _isHtml;
    private bool _syncing = true;
    private string _lastSource;

    public ImageEditDialog(string source)
    {
        if (!ImageSourceSyntax.TryParse(source, out _image))
            throw new ArgumentException("Expected a complete image source.", nameof(source));
        _isHtml = _image.IsHtml;
        _lastSource = source;
        InitializeComponent();
        SourceBox.Text = source;
        AltBox.Text = _image.Alt;
        PathBox.Text = _image.Path;
        _syncing = false;
        Loaded += OnDialogLoaded;
        Closed += OnDialogClosed;
        UpdateValidation();
    }

    public string? Result { get; private set; }
    public string? ShowDialog(Window owner) => ShowModal(owner, () => Result);
    protected override void AssignCancelResult() => Result = null;

    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        SourceBox.Focus();
        SourceBox.CaretIndex = _isHtml ? 4 : 2;
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        Loaded -= OnDialogLoaded;
        Closed -= OnDialogClosed;
    }

    private void OnSourceTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        bool parsed = ImageSourceSyntax.TryParse(SourceBox.Text, out var image) && image.IsHtml == _isHtml;
        bool intact = _isHtml ? ImageSourceSyntax.HasHtmlEnvelope(SourceBox.Text) : parsed;
        if (!intact)
        {
            int caret = e.Changes.FirstOrDefault()?.Offset ?? SourceBox.CaretIndex;
            _syncing = true;
            SourceBox.Text = _lastSource;
            SourceBox.CaretIndex = Math.Min(caret, _lastSource.Length);
            _syncing = false;
            ShowError("不能删除图片结构，请保留 ![]() 或完整的 img 标签");
            return;
        }
        _lastSource = SourceBox.Text;
        if (parsed)
        {
            _image = image;
            _syncing = true;
            AltBox.Text = image.Alt;
            PathBox.Text = image.Path;
            _syncing = false;
        }
        UpdateValidation();
    }

    private void OnFieldTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        string source = ReferenceEquals(sender, AltBox) ? _image.WithAlt(AltBox.Text) : _image.WithPath(PathBox.Text);
        if (!ImageSourceSyntax.TryParse(source, out var image)) return;
        _image = image;
        _lastSource = source;
        _syncing = true;
        SourceBox.Text = source;
        _syncing = false;
        UpdateValidation();
    }

    private bool UpdateValidation()
    {
        bool parsed = ImageSourceSyntax.TryParse(SourceBox.Text, out var image) && image.IsHtml == _isHtml;
        // Keep existing rendering rules: incomplete attributes or unsupported markup cannot be saved.
        var model = parsed ? new MarkdownDocumentModel(SourceBox.Text) : null;
        bool valid = model is not null && model.Spans.Any(span => span.Kind == MarkdownVisualKind.Image &&
            span.SourceStart == 0 && span.SourceLength == model.Markdown.Length);
        AltBox.IsEnabled = parsed;
        PathBox.IsEnabled = parsed;
        SubmitButton.IsEnabled = valid;
        ShowError(valid ? null : "请补全图片源码，并使用支持的图片属性和路径");
        return valid;
    }

    private void ShowError(string? key)
    {
        InteractionState.SetIsInvalid(SourceInputRoot, key is not null);
        if (key is null) Feedback.Clear();
        else Feedback.Show(MemeMomo.Services.LocalizationService.Get(key), MemeMomo.UI.Notifications.NotificationKind.Error);
    }

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e) => DragDialog(e);
    private void OnSubmitClick(object sender, RoutedEventArgs e) => Submit();
    private void OnCancelClick(object sender, RoutedEventArgs e) => CloseDialog(() => Result = null);
    private void Submit()
    {
        if (UpdateValidation()) CloseDialog(() => Result = SourceBox.Text);
    }

    private void OnDialogPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseDialog(() => Result = null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || !SourceBox.IsKeyboardFocused))
        {
            Submit();
            e.Handled = true;
        }
    }
}

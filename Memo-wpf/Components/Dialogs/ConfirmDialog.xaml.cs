using System.Windows;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Window = System.Windows.Window;

namespace Memo.Components.Dialogs;

public partial class ConfirmDialog : MemoDialogWindow
{
    public ConfirmDialog()
        : this(string.Empty, string.Empty)
    {
    }

    public ConfirmDialog(string title, string message, bool isDanger = true)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        SetDangerVisual(isDanger);
        Loaded += OnDialogLoaded;
        Closed += OnDialogClosed;
    }

    public bool Result { get; private set; }
    public bool IsDanger { get; private set; }
    internal Button ConfirmButtonPart => ConfirmButton;

    public bool ShowDialog(Window owner) => ShowModal(owner, () => Result);

    public void SetDangerVisual(bool isDanger)
    {
        IsDanger = isDanger;
        ConfirmButton.SetResourceReference(
            StyleProperty,
            isDanger ? "DialogDangerActionButtonStyle" : "DialogPrimaryActionButtonStyle");
    }

    protected override void AssignCancelResult() => Result = false;

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e) => DragDialog(e);

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        CloseDialog(() => Result = true);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        CloseDialog(() => Result = false);
    }

    private void OnDialogLoaded(object sender, RoutedEventArgs e) => ConfirmButton.Focus();

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        Loaded -= OnDialogLoaded;
        Closed -= OnDialogClosed;
    }

    private void OnDialogPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseDialog(() => Result = false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            CloseDialog(() => Result = true);
            e.Handled = true;
        }
    }
}

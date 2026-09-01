using System.Windows;
using System.Windows.Input;
using Memo.Models;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Window = System.Windows.Window;

namespace Memo.Components.Dialogs;

public partial class CloseActionDialog : MemoDialogWindow
{
    private CloseButtonAction _selectedAction = CloseButtonAction.MinimizeToTray;

    public CloseActionDialog()
    {
        InitializeComponent();
        CloseActionSelector.Options =
        [
            new SegmentedSelectorOption(nameof(CloseButtonAction.MinimizeToTray), "最小化托盘"),
            new SegmentedSelectorOption(nameof(CloseButtonAction.Close), "关闭")
        ];
        CloseActionSelector.SelectedKey = _selectedAction.ToString();
        CloseActionSelector.SelectionChanged += OnSelectionChanged;
        Loaded += OnDialogLoaded;
        Closed += OnDialogClosed;
    }

    public CloseButtonAction? Result { get; private set; }

    public CloseButtonAction? ShowDialog(Window owner) => ShowModal(owner, () => Result);

    protected override void AssignCancelResult() => Result = null;

    private void OnSelectionChanged(object? sender, SegmentedSelectionChangedEventArgs e)
    {
        if (Enum.TryParse(e.NewKey, out CloseButtonAction action))
        {
            _selectedAction = action;
        }
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e) => DragDialog(e);

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        CloseDialog(() => Result = _selectedAction);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        CloseDialog(() => Result = null);
    }

    private void OnDialogLoaded(object sender, RoutedEventArgs e) => CloseActionSelector.Focus();

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        CloseActionSelector.SelectionChanged -= OnSelectionChanged;
        Loaded -= OnDialogLoaded;
        Closed -= OnDialogClosed;
    }

    private void OnDialogPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseDialog(() => Result = null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            CloseDialog(() => Result = _selectedAction);
            e.Handled = true;
        }
    }
}

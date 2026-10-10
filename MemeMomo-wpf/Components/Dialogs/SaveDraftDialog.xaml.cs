using System.Windows;
using System.Windows.Input;

namespace MemeMomo.Components.Dialogs;

public enum SaveDraftResult { Cancel, Discard, Save }

public partial class SaveDraftDialog : MemoDialogWindow
{
    public SaveDraftDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => SaveButton.Focus();
    }

    public SaveDraftResult Result { get; private set; }
    public SaveDraftResult ShowDialog(System.Windows.Window owner) => ShowModal(owner, () => Result);
    protected override void AssignCancelResult() => Result = SaveDraftResult.Cancel;
    private void OnCancel(object sender, RoutedEventArgs e) => CloseDialog(AssignCancelResult);
    private void OnDiscard(object sender, RoutedEventArgs e) => CloseDialog(() => Result = SaveDraftResult.Discard);
    private void OnSave(object sender, RoutedEventArgs e) => CloseDialog(() => Result = SaveDraftResult.Save);
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseDialog(AssignCancelResult); e.Handled = true; }
    }
}

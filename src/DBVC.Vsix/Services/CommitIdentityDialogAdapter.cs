using DBVC.Vsix.UI;

namespace DBVC.Vsix.Services
{
    /// <summary>실제 WPF 창을 띄우는 구현.</summary>
    public sealed class CommitIdentityDialogAdapter : ICommitIdentityDialog
    {
        public CommitIdentityInput? Prompt(string suggestedName, string suggestedEmail)
        {
            var dialog = new CommitIdentityDialog(suggestedName, suggestedEmail)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };

            return dialog.ShowDialog() == true ? dialog.Result : null;
        }
    }
}

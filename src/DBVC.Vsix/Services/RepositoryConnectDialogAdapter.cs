using DBVC.Vsix.UI;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// 실제 WPF 대화상자를 띄우는 구현. 폴더 선택은 기존 어댑터에 위임한다 —
    /// net48 WPF에는 폴더 선택 대화상자가 없어 Windows Forms의 것을 쓰는 사정이 그대로 남는다.
    /// </summary>
    public sealed class RepositoryConnectDialogAdapter : IRepositoryConnectDialog
    {
        private readonly IFolderBrowseDialog _folderDialog;

        public RepositoryConnectDialogAdapter(IFolderBrowseDialog? folderDialog = null)
        {
            _folderDialog = folderDialog ?? new FolderBrowserDialogAdapter();
        }

        public RepositoryConnectRequest? Prompt(string serverName, string databaseName)
        {
            var dialog = new RepositoryConnectDialog(serverName, databaseName, _folderDialog)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };

            return dialog.ShowDialog() == true ? dialog.Result : null;
        }
    }
}

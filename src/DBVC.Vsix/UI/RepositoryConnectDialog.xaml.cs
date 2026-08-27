using System.IO;
using System.Windows;
using System.Windows.Controls;
using DBVC.Core;
using DBVC.Vsix.Services;

namespace DBVC.Vsix.UI
{
    /// <summary>
    /// 저장소 연결 방식을 묻는다. 원격 주소가 쓸 수 있는 것인지는 판정하지 않는다 —
    /// GitManager가 네트워크를 타기 전에 거른다. 여기서도 판정하면 같은 규칙이 두 곳에 생기고
    /// 언젠가 갈라진다.
    /// </summary>
    public partial class RepositoryConnectDialog : Window
    {
        private readonly IFolderBrowseDialog _folderDialog;

        /// <summary>사용자가 폴더 이름을 직접 고쳤는지. 고쳤으면 제안이 덮어쓰지 않는다.</summary>
        private bool _folderNameEditedByUser;

        public RepositoryConnectDialog(string serverName, string databaseName, IFolderBrowseDialog folderDialog)
        {
            InitializeComponent();
            _folderDialog = folderDialog;
            TargetLabel.Text = $"'{serverName}.{databaseName}'의 스크립트를 보관할 Git 저장소를 지정하세요.";
        }

        public RepositoryConnectRequest? Result { get; private set; }

        private void BrowseExisting_Click(object sender, RoutedEventArgs e)
        {
            var path = _folderDialog.PromptForFolder("이미 받아둔 Git 저장소 폴더를 선택하세요.", ExistingPathBox.Text);
            if (path != null) ExistingPathBox.Text = path;
        }

        private void BrowseParent_Click(object sender, RoutedEventArgs e)
        {
            var path = _folderDialog.PromptForFolder("저장소를 받을 상위 폴더를 선택하세요.", ParentFolderBox.Text);
            if (path != null) ParentFolderBox.Text = path;
        }

        private void RemoteUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (FolderNameBox == null || _folderNameEditedByUser) return;

            var suggested = RemoteUrlNaming.SuggestFolderName(RemoteUrlBox.Text) ?? string.Empty;

            // 제안이 만든 변경은 사용자가 고친 것으로 세면 안 된다.
            _folderNameEditedByUser = true;
            FolderNameBox.Text = suggested;
            _folderNameEditedByUser = false;
        }

        private void FolderName_TextChanged(object sender, TextChangedEventArgs e)
        {
            _folderNameEditedByUser = true;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (ExistingChoice.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(ExistingPathBox.Text))
                {
                    ShowError("연결할 폴더를 선택하세요.");
                    return;
                }

                Result = RepositoryConnectRequest.ForExistingFolder(ExistingPathBox.Text.Trim());
                DialogResult = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(RemoteUrlBox.Text))
            {
                ShowError("원격 주소를 입력하세요.");
                return;
            }

            if (string.IsNullOrWhiteSpace(ParentFolderBox.Text) || string.IsNullOrWhiteSpace(FolderNameBox.Text))
            {
                ShowError("받을 위치와 폴더 이름을 모두 지정하세요.");
                return;
            }

            var target = Path.Combine(ParentFolderBox.Text.Trim(), FolderNameBox.Text.Trim());
            Result = RepositoryConnectRequest.ForClone(RemoteUrlBox.Text.Trim(), target);
            DialogResult = true;
        }

        private void ShowError(string message)
        {
            ErrorLabel.Text = message;
            ErrorLabel.Visibility = Visibility.Visible;
        }
    }
}

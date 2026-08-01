namespace DBVC.Vsix.Services
{
    /// <summary>
    /// 폴더를 선택받는다. ViewModel이 대화상자 구현에 직접 의존하지 않도록 분리한다.
    /// </summary>
    public interface IFolderBrowseDialog
    {
        /// <summary>사용자가 선택한 폴더 경로. 취소하면 <c>null</c>.</summary>
        string? PromptForFolder(string description, string? initialPath);
    }

    /// <summary>
    /// net48 WPF에는 폴더 선택 대화상자가 없어 Windows Forms의 것을 쓴다.
    /// </summary>
    public class FolderBrowserDialogAdapter : IFolderBrowseDialog
    {
        public string? PromptForFolder(string description, string? initialPath)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = description,
                ShowNewFolderButton = false,
                SelectedPath = initialPath ?? string.Empty
            };

            return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
        }
    }
}

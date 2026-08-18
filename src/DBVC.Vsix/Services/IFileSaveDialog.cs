namespace DBVC.Vsix.Services
{
    /// <summary>
    /// 저장 위치를 사용자에게 묻는다. ViewModel이 WPF 대화상자에 직접 의존하지 않도록 분리한다.
    /// </summary>
    public interface IFileSaveDialog
    {
        /// <summary>사용자가 선택한 경로. 취소하면 <c>null</c>.</summary>
        string? PromptForSavePath(string title, string defaultFileName);
    }

    public class SaveFileDialogAdapter : IFileSaveDialog
    {
        public string? PromptForSavePath(string title, string defaultFileName)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = title,
                FileName = defaultFileName,
                DefaultExt = ".sql",
                Filter = "SQL 스크립트 (*.sql)|*.sql|모든 파일 (*.*)|*.*",
                OverwritePrompt = true
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }
    }
}

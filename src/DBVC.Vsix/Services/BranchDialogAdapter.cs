using System.Collections.Generic;
using DBVC.Core.Models;
using DBVC.Vsix.UI;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// 실제 WPF 창을 띄우는 구현. CommitIdentityDialogAdapter와 같은 이유로 호출마다 새
    /// BranchDialog를 만든다 - WPF Window는 한 번 닫히면 다시 ShowDialog할 수 없으므로,
    /// 세션 내내 하나의 창 인스턴스를 재사용하면 두 번째 호출부터 예외가 난다.
    /// </summary>
    public sealed class BranchDialogAdapter : IBranchDialog
    {
        public string? AskNewName()
        {
            var dialog = new BranchDialog
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            dialog.SetupNewName();

            return dialog.ShowDialog() == true ? dialog.NewName : null;
        }

        public string? AskExisting(IReadOnlyList<BranchInfo> branches)
        {
            var dialog = new BranchDialog
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            dialog.SetupExisting(branches);

            return dialog.ShowDialog() == true ? dialog.SelectedName : null;
        }
    }
}

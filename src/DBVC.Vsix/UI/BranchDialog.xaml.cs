using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DBVC.Core.Models;
using DBVC.Vsix.Services;

namespace DBVC.Vsix.UI
{
    /// <summary>
    /// 브랜치 이름 입력과 고르기를 한 창에서 처리한다. 두 모드를 Visibility로 가르는 이유는
    /// CommitIdentityDialog와 같은 가벼운 어댑터 패턴(창 자체가 IBranchDialog 구현)을
    /// 유지하기 위해서다 - 대화상자를 둘로 나누면 조립 루트에 인스턴스가 두 개 생긴다.
    /// </summary>
    public partial class BranchDialog : Window, IBranchDialog
    {
        /// <summary>목록에 그대로 뿌릴 한 줄. BranchInfo에 표시 문자열을 넣지 않는 이유는
        /// Core가 화면 문구를 갖지 않는다는 규약 때문이다.</summary>
        private sealed class Row
        {
            public string Name { get; set; } = string.Empty;
            public string Display { get; set; } = string.Empty;
        }

        public BranchDialog()
        {
            InitializeComponent();
        }

        public string? AskNewName()
        {
            NewNamePanel.Visibility = Visibility.Visible;
            PickPanel.Visibility = Visibility.Collapsed;
            NameBox.Focus();
            return ShowDialog() == true ? NameBox.Text?.Trim() : null;
        }

        public string? AskExisting(IReadOnlyList<BranchInfo> branches)
        {
            NewNamePanel.Visibility = Visibility.Collapsed;
            PickPanel.Visibility = Visibility.Visible;

            BranchList.ItemsSource = branches.Select(b => new Row
            {
                Name = b.Name,
                Display = b.Name
                    + (b.IsCurrent ? "  (현재)" : string.Empty)
                    + (b.IsRemoteOnly ? "  (원격)" : string.Empty)
            }).ToList();

            return ShowDialog() == true ? (BranchList.SelectedItem as Row)?.Name : null;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            // 빈 이름·미선택으로 닫혀도 여기서 막지 않는다 - 호출자(ViewModel)가 빈 값을 받아
            // 조용히 아무것도 하지 않는다. 이름 형식 판정을 여기서 하지 않는 이유는
            // CommitIdentityDialog가 GitIdentity.Validate를 Core에 맡기는 이유와 같다.
            DialogResult = true;
        }
    }
}

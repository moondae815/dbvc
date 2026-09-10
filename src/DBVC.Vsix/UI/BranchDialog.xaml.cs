using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DBVC.Core.Models;

namespace DBVC.Vsix.UI
{
    /// <summary>
    /// 브랜치 이름 입력과 고르기를 한 창에서 처리한다. 두 모드를 Visibility로 가른다.
    ///
    /// 이 창은 IBranchDialog를 구현하지 않는다 - WPF Window는 한 번 닫히면 ShowDialog를 다시
    /// 부를 수 없다("Cannot set Visibility to Visible or call Show, ShowDialog, or
    /// WindowInteropHelper.EnsureHandle after a Window has closed"). 세션 내내 하나의 인스턴스를
    /// 재사용하는 서비스가 이 창 자체라면, 첫 호출 뒤에는 이 창을 쓰는 모든 명령이 예외를 던진다.
    /// 그래서 IBranchDialog 구현은 CommitIdentityDialogAdapter와 같은 방식으로
    /// BranchDialogAdapter가 맡고, 그 어댑터가 호출마다 새 창을 만든다.
    /// </summary>
    public partial class BranchDialog : Window
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

        /// <summary>새 이름 모드로 화면을 채운다. 실제로 띄우는 것은 어댑터의 ShowDialog 호출이다.</summary>
        public void SetupNewName()
        {
            NewNamePanel.Visibility = Visibility.Visible;
            PickPanel.Visibility = Visibility.Collapsed;
            NameBox.Focus();
        }

        /// <summary>고르기 모드로 화면을 채운다.</summary>
        public void SetupExisting(IReadOnlyList<BranchInfo> branches)
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
        }

        /// <summary>새 이름 모드에서 입력한 값. 취소하거나 비웠으면 빈 문자열이다.</summary>
        public string NewName => NameBox.Text?.Trim() ?? string.Empty;

        /// <summary>고르기 모드에서 고른 브랜치 이름. 아무것도 고르지 않았으면 null이다.</summary>
        public string? SelectedName => (BranchList.SelectedItem as Row)?.Name;

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            // 빈 이름·미선택으로 닫혀도 여기서 막지 않는다 - 호출자(어댑터를 거친 ViewModel)가
            // 빈 값을 받아 조용히 아무것도 하지 않는다. 이름 형식 판정을 여기서 하지 않는 이유는
            // CommitIdentityDialog가 GitIdentity.Validate를 Core에 맡기는 이유와 같다.
            DialogResult = true;
        }
    }
}

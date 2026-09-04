using System.Windows;
using DBVC.Core;
using DBVC.Vsix.Services;

namespace DBVC.Vsix.UI
{
    /// <summary>
    /// 커밋 작성자를 입력받는다. 형식 판정은 Core의 GitIdentity.Validate가 한다 -
    /// 여기서도 판정하면 같은 규칙이 두 곳에 생기고 언젠가 갈라진다.
    /// </summary>
    public partial class CommitIdentityDialog : Window
    {
        public CommitIdentityDialog(string suggestedName, string suggestedEmail)
        {
            InitializeComponent();
            NameBox.Text = suggestedName ?? string.Empty;
            EmailBox.Text = suggestedEmail ?? string.Empty;
        }

        public CommitIdentityInput? Result { get; private set; }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var reason = GitIdentity.Validate(NameBox.Text, EmailBox.Text);
            if (reason != null)
            {
                ErrorLabel.Text = reason;
                ErrorLabel.Visibility = Visibility.Visible;
                return;
            }

            Result = new CommitIdentityInput { Name = NameBox.Text.Trim(), Email = EmailBox.Text.Trim() };
            DialogResult = true;
        }
    }
}

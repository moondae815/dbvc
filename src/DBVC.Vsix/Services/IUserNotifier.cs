using System.Windows;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// 사용자에게 결과를 알리고 진행 여부를 묻는다. ViewModel이 WPF에 직접 의존하지 않도록 분리한다.
    /// </summary>
    public interface IUserNotifier
    {
        void ShowError(string title, string message);

        /// <summary>완료·요약처럼 오류가 아닌 결과를 알린다.</summary>
        void ShowInfo(string title, string message);

        /// <summary>진행 여부를 묻는다. 사용자가 계속을 선택하면 <c>true</c>.</summary>
        bool Confirm(string title, string message);
    }

    /// <summary>
    /// 설계에 명시된 대로 WPF <c>MessageBox</c>로 표시한다.
    /// </summary>
    public class MessageBoxNotifier : IUserNotifier
    {
        public void ShowError(string title, string message)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        public void ShowInfo(string title, string message)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public bool Confirm(string title, string message)
        {
            // 되돌릴 수 없는 손실을 경고하는 자리이므로 Warning 아이콘을 쓴다.
            // 기본 선택도 Cancel로 둔다 - Enter를 무심코 누르면 데이터 손실 경고를 그냥 진행시켜 버린다.
            return MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
                == MessageBoxResult.OK;
        }
    }
}

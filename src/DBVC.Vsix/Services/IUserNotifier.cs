using System.Windows;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// 사용자에게 오류를 알린다. ViewModel이 WPF에 직접 의존하지 않도록 분리한다.
    /// </summary>
    public interface IUserNotifier
    {
        void ShowError(string title, string message);
    }

    /// <summary>
    /// 설계에 명시된 대로 WPF <c>MessageBox</c>로 오류를 표시한다.
    /// </summary>
    public class MessageBoxNotifier : IUserNotifier
    {
        public void ShowError(string title, string message)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

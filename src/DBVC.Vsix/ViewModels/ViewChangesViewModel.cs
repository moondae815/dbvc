using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DBVC.Vsix.ViewModels
{
    public class ViewChangesViewModel : INotifyPropertyChanged
    {
        private string? _commitMessage;
        public string? CommitMessage
        {
            get => _commitMessage;
            set
            {
                _commitMessage = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

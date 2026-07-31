using System.Collections.ObjectModel;
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

        public ObservableCollection<ChangeItemViewModel> Changes { get; } = new ObservableCollection<ChangeItemViewModel>();

        private ChangeItemViewModel? _selectedChange;
        public ChangeItemViewModel? SelectedChange
        {
            get => _selectedChange;
            set
            {
                _selectedChange = value;
                OnPropertyChanged();
            }
        }

        public System.Windows.Input.ICommand RefreshCommand { get; }

        public ViewChangesViewModel()
        {
            RefreshCommand = new Commands.RelayCommand(Refresh);
        }

        public void Refresh()
        {
            Changes.Clear();
            // Real implementation will call StateTracker, left for next tasks or integration
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

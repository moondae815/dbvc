using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DBVC.Vsix.ViewModels
{
    public class ChangeItemViewModel : INotifyPropertyChanged
    {
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public string? ObjectName { get; set; }
        public string? State { get; set; } // "Modified", "Added", "Deleted"

        /// <summary><c>dbo/Tables/Users.sql</c> 형태의 저장소 상대 경로. 커밋·Diff 대상 식별에 쓴다.</summary>
        public string? RelativePath { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

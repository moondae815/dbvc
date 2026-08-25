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
        public string? ObjectType { get; set; }

        public string ObjectTypeText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(ObjectType))
                    return string.Empty;

                var upperType = ObjectType!.Trim().ToUpperInvariant();
                return upperType switch
                {
                    "PROCEDURE" => "SP",
                    "FUNCTION" => "UDF",
                    "TABLE" => "Table",
                    "VIEW" => "View",
                    "TRIGGER" => "Trigger",
                    _ => char.ToUpper(upperType[0]) + upperType.Substring(1).ToLowerInvariant()
                };
            }
        }

        public string? State { get; set; } // "Modified", "Added", "Deleted"

        /// <summary>
        /// 화면에 뿌리는 상태. Core의 <see cref="State"/>는 데이터로 남긴다 —
        /// WorkingTreeCleaner가 삭제 판정에 쓰고 Core 테스트가 문자열로 검증한다.
        ///
        /// 번역표에 없는 값은 원문을 그대로 통과시킨다. 조용히 빈칸이 되면
        /// Core가 새 상태를 내놓기 시작해도 알아챌 방법이 없다.
        /// </summary>
        public string StateText => State switch
        {
            "Added" => "추가",
            "Modified" => "수정",
            "Deleted" => "삭제",
            _ => State ?? string.Empty
        };

        /// <summary>
        /// 목록에 띄울 변경자. 공용 계정 환경에서는 로그인 이름이 전부 같으므로
        /// 접속 PC를 우선한다 - 로그인 이름을 대면 아무 정보도 주지 못한다.
        /// </summary>
        public string? Author { get; set; }

        /// <summary><c>dbo/Tables/Users.sql</c> 형태의 저장소 상대 경로. 커밋·Diff 대상 식별에 쓴다.</summary>
        public string? RelativePath { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

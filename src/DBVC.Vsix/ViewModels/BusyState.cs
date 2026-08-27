using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DBVC.Vsix.ViewModels
{
    /// <summary>
    /// 백그라운드 작업 하나의 상태. 변경 목록 화면과 배포·감사 화면이 같은 인스턴스를 본다.
    ///
    /// 나누면 도구 줄에 진행 표시가 둘, 취소 버튼이 둘 생기고 사용자가 무엇이 도는지 알 수
    /// 없게 된다. 자식 ViewModel이 부모를 역참조하는 것보다 이쪽이 낫다 — 순환 참조가 없고
    /// 둘 다 이것 하나만 테스트하면 된다.
    ///
    /// <b>UI 스레드에서만 바꾼다.</b> 보고는 백그라운드에서 오므로 호출부가
    /// <c>IBackgroundScheduler.Post</c>로 넘긴 뒤에 만져야 한다.
    /// </summary>
    public class BusyState : INotifyPropertyChanged
    {
        private bool _isBusy;
        private bool _isCancellable;
        private string? _progressText;

        /// <summary>진행 중에는 모든 동작 버튼이 잠긴다. 겹쳐 돌면 서로의 결과를 덮어쓴다.</summary>
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (_isBusy == value) return;
                _isBusy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNotBusy));
                RaiseChanged();
            }
        }

        /// <summary>버튼은 CanExecute가 잠그지만 체크박스에는 명령이 없다. 화면이 막을 근거다.</summary>
        public bool IsNotBusy => !IsBusy;

        /// <summary>
        /// 지금 걸려 있는 작업을 취소가 실제로 멈출 수 있는지.
        ///
        /// <see cref="IsBusy"/>만으로 취소 버튼을 띄우면 안 된다. Cancel이 취소하는 것은 취소
        /// 토큰이 걸린 작업(추출, 저장소 받기의 전송 단계, 차이 검사)뿐인데 연결·커밋·Pull·
        /// Push도 IsBusy를 세운다 — 그때 버튼이 뜨면 눌러도 아무 일이 없고 "취소하는 중..."만
        /// 남는다. 없는 취소를 있는 척하는 버튼보다 없는 편이 정직하다.
        ///
        /// 두 ViewModel이 이 인스턴스를 공유하는 지금은 함정이 더 넓다 — 한쪽이 세운 IsBusy를
        /// 다른 쪽의 취소 버튼이 보게 되므로, 작업을 세우는 자리마다 이 값을 함께 정한다.
        /// </summary>
        public bool IsCancellable
        {
            get => _isCancellable;
            set
            {
                if (_isCancellable == value) return;
                _isCancellable = value;
                OnPropertyChanged();
                RaiseChanged();
            }
        }

        /// <summary>진행 표시 옆에 붙는 한 줄. 작업이 없으면 null이다.</summary>
        public string? ProgressText
        {
            get => _progressText;
            set
            {
                if (_progressText == value) return;
                _progressText = value;
                OnPropertyChanged();
                RaiseChanged();
            }
        }

        /// <summary>어느 값이든 바뀌면 오른다. 두 ViewModel이 CanExecute를 다시 거는 자리다.</summary>
        public event EventHandler? Changed;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

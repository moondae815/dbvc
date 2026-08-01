using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DBVC.Core;
using DBVC.Core.Models;
using DBVC.Vsix.Commands;
using DBVC.Vsix.Services;

namespace DBVC.Vsix.ViewModels
{
    /// <summary>
    /// View Changes 도구 창의 ViewModel.
    /// 활성 데이터베이스의 변경 목록을 보여주고 스테이징·커밋을 처리한다.
    /// </summary>
    public class ViewChangesViewModel : INotifyPropertyChanged
    {
        private const string NotMappedWarning = "Active Database is not mapped to a Git repository.";

        private readonly IConfigManager _configManager;
        private readonly IStateTracker _stateTracker;
        private readonly IGitManager _gitManager;
        private readonly ISmoManager _smoManager;
        private readonly IUserNotifier _notifier;

        /// <summary>새로고침 시점의 변경 레코드. 커밋 후 처리 완료 표시에 사용한다.</summary>
        private IReadOnlyList<ChangeRecord> _lastChangeRecords = new List<ChangeRecord>();

        public ViewChangesViewModel()
            : this(new ConfigManager(), null, null, null, null)
        {
        }

        public ViewChangesViewModel(
            IConfigManager configManager,
            IStateTracker? stateTracker,
            IGitManager? gitManager,
            ISmoManager? smoManager,
            IUserNotifier? notifier)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _gitManager = gitManager ?? new GitManager(_configManager);
            _stateTracker = stateTracker ?? new StateTracker(_configManager, _gitManager);
            _smoManager = smoManager ?? new SmoManager(_configManager);
            _notifier = notifier ?? new MessageBoxNotifier();

            RefreshCommand = new RelayCommand(Refresh);
            SetupCommand = new RelayCommand(Setup);
            CommitCommand = new RelayCommand(Commit, CanCommit);
            ConnectCommand = new RelayCommand(() => SetContext(ServerName, DatabaseName), () => HasContext);
        }

        // ---------- 연결 컨텍스트 ----------

        private string? _serverName;
        public string? ServerName
        {
            get => _serverName;
            set
            {
                if (_serverName == value) return;
                _serverName = value;
                OnPropertyChanged();
                RaiseConnectCanExecuteChanged();
            }
        }

        private string? _databaseName;
        public string? DatabaseName
        {
            get => _databaseName;
            set
            {
                if (_databaseName == value) return;
                _databaseName = value;
                OnPropertyChanged();
                RaiseConnectCanExecuteChanged();
            }
        }

        private bool HasContext => !string.IsNullOrWhiteSpace(ServerName) && !string.IsNullOrWhiteSpace(DatabaseName);

        /// <summary>
        /// 활성 데이터베이스를 지정하고 매핑/초기화 상태를 다시 판정한다.
        /// </summary>
        public void SetContext(string? serverName, string? databaseName)
        {
            ServerName = serverName;
            DatabaseName = databaseName;

            Changes.Clear();
            _lastChangeRecords = new List<ChangeRecord>();

            if (!HasContext)
            {
                IsMapped = false;
                IsInitialized = false;
                WarningMessage = null;
                return;
            }

            IsMapped = _configManager.TryGetMapping(ServerName!, DatabaseName!) != null;
            WarningMessage = IsMapped ? null : NotMappedWarning;

            IsInitialized = _stateTracker.IsInitialized(BuildConnectionString());

            if (IsMapped && IsInitialized)
            {
                Refresh();
            }
        }

        private string BuildConnectionString()
        {
            return StateTracker.BuildConnectionString(ServerName!, DatabaseName!);
        }

        // ---------- 바인딩 속성 ----------

        private bool _isInitialized;
        public bool IsInitialized
        {
            get => _isInitialized;
            set
            {
                if (_isInitialized == value) return;
                _isInitialized = value;
                OnPropertyChanged();
            }
        }

        private bool _isMapped;
        public bool IsMapped
        {
            get => _isMapped;
            set
            {
                if (_isMapped == value) return;
                _isMapped = value;
                OnPropertyChanged();
                RaiseCommitCanExecuteChanged();
            }
        }

        private string? _warningMessage;
        public string? WarningMessage
        {
            get => _warningMessage;
            set
            {
                if (_warningMessage == value) return;
                _warningMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasWarning));
            }
        }

        public bool HasWarning => !string.IsNullOrEmpty(WarningMessage);

        private string? _commitMessage;
        public string? CommitMessage
        {
            get => _commitMessage;
            set
            {
                if (_commitMessage == value) return;
                _commitMessage = value;
                OnPropertyChanged();
                RaiseCommitCanExecuteChanged();
            }
        }

        public ObservableCollection<ChangeItemViewModel> Changes { get; } = new ObservableCollection<ChangeItemViewModel>();

        private ChangeItemViewModel? _selectedChange;
        public ChangeItemViewModel? SelectedChange
        {
            get => _selectedChange;
            set
            {
                if (ReferenceEquals(_selectedChange, value)) return;
                _selectedChange = value;
                OnPropertyChanged();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>선택된 객체가 바뀌면 뷰가 Diff를 다시 렌더링하도록 알린다.</summary>
        public event EventHandler? SelectionChanged;

        public ICommand RefreshCommand { get; }
        public ICommand SetupCommand { get; }
        public ICommand CommitCommand { get; }

        /// <summary>
        /// 입력된 서버/데이터베이스를 활성 컨텍스트로 적용한다.
        /// SSMS Object Explorer 연동이 붙기 전까지 사용자가 대상 DB를 지정하는 경로다.
        /// </summary>
        public ICommand ConnectCommand { get; }

        // ---------- Setup ----------

        private void Setup()
        {
            if (!HasContext)
            {
                _notifier.ShowError("DBVC", "먼저 Object Explorer에서 대상 데이터베이스를 선택하세요.");
                return;
            }

            try
            {
                _stateTracker.InitializeDatabase(BuildConnectionString());
            }
            catch (Exception ex)
            {
                // 설치 실패(권한 부족 등)를 초기화 성공으로 위장해서는 안 된다.
                _notifier.ShowError("DBVC 설치 실패", ex.Message);
                return;
            }

            IsInitialized = true;
            Refresh();
        }

        // ---------- Refresh ----------

        public void Refresh()
        {
            Changes.Clear();
            _lastChangeRecords = new List<ChangeRecord>();
            RaiseCommitCanExecuteChanged();

            if (!HasContext) return;

            if (!IsMapped)
            {
                WarningMessage = NotMappedWarning;
                return;
            }

            var warnings = new List<string>();

            try
            {
                // 현재 DB 상태를 파일로 추출해야 Git 상태·Diff가 최신 코드를 반영한다.
                var scriptResult = _smoManager.ScriptObjectsDetailed(ServerName!, DatabaseName!, null);
                if (scriptResult == null)
                {
                    warnings.Add("데이터베이스에서 객체를 추출하지 못했습니다.");
                }
                else if (scriptResult.HasFailures)
                {
                    warnings.Add($"일부 객체를 추출하지 못했습니다: {string.Join(", ", scriptResult.FailedObjects)}");
                }

                if (!_stateTracker.RefreshState(ServerName!, DatabaseName!))
                {
                    warnings.Add("변경 로그를 읽지 못했습니다.");
                }

                _lastChangeRecords = _stateTracker.GetPendingChanges(ServerName!, DatabaseName!);
                foreach (var record in _lastChangeRecords)
                {
                    Changes.Add(new ChangeItemViewModel
                    {
                        ObjectName = record.QualifiedName,
                        State = record.State,
                        RelativePath = record.RelativePath,
                        IsSelected = true
                    });
                }
            }
            catch (Exception ex)
            {
                _notifier.ShowError("DBVC 새로고침 실패", ex.Message);
            }

            WarningMessage = warnings.Count > 0 ? string.Join(" / ", warnings) : null;
            RaiseCommitCanExecuteChanged();
        }

        // ---------- Commit ----------

        private bool CanCommit()
        {
            return HasContext
                && IsMapped
                && IsInitialized
                && !string.IsNullOrWhiteSpace(CommitMessage)
                && Changes.Any(c => c.IsSelected);
        }

        private void Commit()
        {
            if (!CanCommit()) return;

            var selected = Changes.Where(c => c.IsSelected).ToList();
            var selectedPaths = selected
                .Select(c => c.RelativePath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .ToList();

            try
            {
                if (!_gitManager.CommitChanges(ServerName!, DatabaseName!, CommitMessage!, selectedPaths))
                {
                    WarningMessage = "커밋할 변경사항이 없습니다.";
                    return;
                }

                var committedNames = new HashSet<string>(selected.Select(c => c.ObjectName ?? string.Empty), StringComparer.OrdinalIgnoreCase);
                _stateTracker.MarkProcessed(
                    ServerName!,
                    DatabaseName!,
                    _lastChangeRecords.Where(r => committedNames.Contains(r.QualifiedName)));

                CommitMessage = string.Empty;
                Refresh();
            }
            catch (Exception ex)
            {
                _notifier.ShowError("DBVC 커밋 실패", ex.Message);
            }
        }

        private void RaiseCommitCanExecuteChanged()
        {
            (CommitCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void RaiseConnectCanExecuteChanged()
        {
            (ConnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            RaiseCommitCanExecuteChanged();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

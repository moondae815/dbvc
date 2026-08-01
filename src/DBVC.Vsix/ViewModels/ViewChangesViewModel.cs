using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
        private readonly IFileSaveDialog _saveDialog;
        private readonly IFolderBrowseDialog _folderDialog;
        private readonly IWorkingTreeCleaner _cleaner;
        private readonly ScriptExporter _scriptExporter;

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
            IUserNotifier? notifier,
            IFileSaveDialog? saveDialog = null,
            IWorkingTreeCleaner? cleaner = null,
            IFolderBrowseDialog? folderDialog = null)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _gitManager = gitManager ?? new GitManager(_configManager);
            _stateTracker = stateTracker ?? new StateTracker(_configManager, _gitManager);
            _smoManager = smoManager ?? new SmoManager(_configManager);
            _notifier = notifier ?? new MessageBoxNotifier();
            _saveDialog = saveDialog ?? new SaveFileDialogAdapter();
            _cleaner = cleaner ?? new WorkingTreeCleaner();
            _folderDialog = folderDialog ?? new FolderBrowserDialogAdapter();
            _scriptExporter = new ScriptExporter(_configManager, _gitManager);

            RefreshCommand = new RelayCommand(Refresh);
            SetupCommand = new RelayCommand(Setup);
            CommitCommand = new RelayCommand(Commit, CanCommit);
            ConnectCommand = new RelayCommand(() => SetContext(ServerName, DatabaseName), () => HasContext);
            ConnectRepositoryCommand = new RelayCommand(ConnectRepository, CanConnectRepository);
            GenerateDeploymentScriptCommand = new RelayCommand(() => GenerateScript(ScriptKind.Deployment), CanGenerateScript);
            GenerateRollbackScriptCommand = new RelayCommand(() => GenerateScript(ScriptKind.Rollback), CanGenerateScript);
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
                RaiseActionCanExecuteChanged();
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
                RaiseActionCanExecuteChanged();
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

        /// <summary>
        /// 활성 데이터베이스에 Git 저장소를 매핑한다.
        /// 매핑이 없으면 추출도 커밋도 불가능하므로 여기가 첫 설정 경로다.
        /// </summary>
        public ICommand ConnectRepositoryCommand { get; }

        /// <summary>선택된 객체들의 현재 DDL을 단일 스크립트로 내보낸다. (Feature 8)</summary>
        public ICommand GenerateDeploymentScriptCommand { get; }

        /// <summary>선택된 객체들의 마지막 커밋 직전 코드를 단일 스크립트로 내보낸다. (Feature 9)</summary>
        public ICommand GenerateRollbackScriptCommand { get; }

        // ---------- 저장소 매핑 ----------

        private bool CanConnectRepository() => HasContext && !IsMapped;

        private void ConnectRepository()
        {
            if (!CanConnectRepository()) return;

            var path = _folderDialog.PromptForFolder(
                $"'{ServerName}.{DatabaseName}'의 스크립트를 보관할 Git 저장소 폴더를 선택하세요.", null);

            // 사용자가 취소한 경우다. 오류가 아니다.
            if (string.IsNullOrWhiteSpace(path)) return;

            if (!_gitManager.IsRepository(path!))
            {
                // 유효하지 않은 경로를 저장하면 이후 모든 동작이 조용히 실패한다.
                _notifier.ShowError("DBVC", $"'{path}'은(는) Git 저장소가 아닙니다. git init된 폴더를 선택하세요.");
                return;
            }

            _configManager.AddMapping(ServerName!, DatabaseName!, path!);

            // 매핑·초기화 상태를 다시 판정하고 목록을 새로고침한다.
            SetContext(ServerName, DatabaseName);
        }

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
            RaiseActionCanExecuteChanged();

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

                // DROP된 객체의 파일을 지워야 Git이 삭제를 감지하고 커밋에 포함할 수 있다.
                // RefreshState가 Git 상태를 읽은 뒤이므로 이 정리가 목록 판정을 바꾸지 않는다.
                var mapping = _configManager.TryGetMapping(ServerName!, DatabaseName!);
                if (mapping != null)
                {
                    var cleanup = _cleaner.RemoveDeletedObjectFiles(mapping.GitPath, _lastChangeRecords);
                    if (cleanup.HasFailures)
                    {
                        warnings.Add($"삭제된 객체의 파일을 지우지 못했습니다: {string.Join(", ", cleanup.FailedPaths)}");
                    }
                }

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
            RaiseActionCanExecuteChanged();
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

        // ---------- 외부에서 객체 선택 (SQL 에디터 컨텍스트 메뉴) ----------

        /// <summary>
        /// 지정한 객체를 변경 목록에서 찾아 선택한다.
        /// 찾지 못하면 기존 선택을 유지하고 false를 반환한다.
        /// </summary>
        public bool TrySelectObject(string? schema, string name)
        {
            var match = ObjectNameResolver.FindMatch(_lastChangeRecords, schema, name);
            if (match == null) return false;

            var item = Changes.FirstOrDefault(c =>
                string.Equals(c.ObjectName, match.QualifiedName, StringComparison.OrdinalIgnoreCase));
            if (item == null) return false;

            SelectedChange = item;
            return true;
        }

        // ---------- Deployment / Rollback 스크립트 ----------

        private bool CanGenerateScript()
        {
            // 커밋과 달리 커밋 메시지는 필요 없다.
            return HasContext && IsMapped && IsInitialized && Changes.Any(c => c.IsSelected);
        }

        private void GenerateScript(ScriptKind kind)
        {
            if (!CanGenerateScript()) return;

            var result = _scriptExporter.Export(
                ServerName!, DatabaseName!, GetSelectedRecords(), kind, DateTimeOffset.Now);

            if (!result.HasContent)
            {
                WarningMessage = result.ExcludedObjects.Count > 0
                    ? $"내보낼 내용이 없습니다. 제외된 객체: {string.Join(", ", result.ExcludedObjects)}"
                    : "내보낼 내용이 없습니다.";
                return;
            }

            var kindLabel = kind == ScriptKind.Rollback ? "Rollback" : "Deployment";
            var targetPath = _saveDialog.PromptForSavePath(
                $"DBVC {kindLabel} Script 저장",
                $"DBVC_{kindLabel}_{DatabaseName}.sql");

            // 사용자가 취소한 경우다. 오류가 아니다.
            if (string.IsNullOrWhiteSpace(targetPath)) return;

            try
            {
                File.WriteAllText(targetPath!, result.Script);
            }
            catch (Exception ex)
            {
                _notifier.ShowError($"DBVC {kindLabel} Script 저장 실패", ex.Message);
                return;
            }

            WarningMessage = result.ExcludedObjects.Count > 0
                ? $"{result.IncludedCount}개 객체를 내보냈습니다. 제외된 객체: {string.Join(", ", result.ExcludedObjects)}"
                : null;
        }

        /// <summary>체크된 항목에 대응하는 변경 레코드를 돌려준다.</summary>
        private List<ChangeRecord> GetSelectedRecords()
        {
            var selected = Changes.Where(c => c.IsSelected).ToList();
            var byName = _lastChangeRecords.ToDictionary(r => r.QualifiedName, StringComparer.OrdinalIgnoreCase);

            var records = new List<ChangeRecord>();
            foreach (var item in selected)
            {
                if (item.ObjectName != null && byName.TryGetValue(item.ObjectName, out var record))
                {
                    records.Add(record);
                    continue;
                }

                // 새로고침 이후 추가된 항목은 화면의 정보만으로 구성한다.
                records.Add(new ChangeRecord
                {
                    QualifiedName = item.ObjectName ?? string.Empty,
                    RelativePath = item.RelativePath ?? string.Empty,
                    State = item.State ?? string.Empty
                });
            }
            return records;
        }

        private void RaiseActionCanExecuteChanged()
        {
            (CommitCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GenerateDeploymentScriptCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GenerateRollbackScriptCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ConnectRepositoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void RaiseConnectCanExecuteChanged()
        {
            (ConnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            RaiseActionCanExecuteChanged();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

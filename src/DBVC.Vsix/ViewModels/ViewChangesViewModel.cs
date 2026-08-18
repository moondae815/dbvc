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
        // "매핑"은 ConfigManager의 내부 용어다. 바로 옆에 붙는 버튼이 "저장소 연결..."이므로
        // 배너도 같은 말을 써야 무엇을 눌러야 하는지 문장 하나로 전해진다.
        private const string NotMappedWarning = "현재 데이터베이스에 연결된 Git 저장소가 없습니다.";

        private readonly IConfigManager _configManager;
        private readonly ISqlCredentialStore _credentialStore;
        private readonly ISsmsConnectionSource? _ssmsConnectionSource;
        private readonly IStateTracker _stateTracker;
        private readonly IGitManager _gitManager;
        private readonly ISmoManager _smoManager;
        private readonly IUserNotifier _notifier;
        private readonly IFileSaveDialog _saveDialog;
        private readonly IFolderBrowseDialog _folderDialog;
        private readonly IWorkingTreeCleaner _cleaner;
        private readonly ScriptExporter _scriptExporter;
        private readonly IBackgroundScheduler _scheduler;

        /// <summary>새로고침 시점의 변경 레코드. 커밋 후 처리 완료 표시에 사용한다.</summary>
        private IReadOnlyList<ChangeRecord> _lastChangeRecords = new List<ChangeRecord>();

        /// <summary>
        /// 마지막 새로고침에서 작업 트리 정리(삭제된 객체 파일 제거)에 실패한 상대 경로.
        /// 체크박스만으로는 충분하지 않다 — 사용자가 경고를 무시하고 다시 체크할 수 있으므로,
        /// Commit에서도 이 목록을 근거로 한 번 더 걸러야 "삭제가 조용히 사라지는" 결함이 재발하지 않는다.
        /// </summary>
        private readonly HashSet<string> _failedCleanupPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            IFolderBrowseDialog? folderDialog = null,
            ISqlCredentialStore? credentialStore = null,
            ISsmsConnectionSource? ssmsConnectionSource = null,
            IBackgroundScheduler? scheduler = null)
        {
            // 기본값이 인라인인 이유: 단위 테스트와 셸 밖 실행이 이 경로다.
            // 실제 도구 창에는 DbvcServices가 UI 스레드를 비우는 구현을 넣어 준다.
            _scheduler = scheduler ?? new InlineBackgroundScheduler();
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _credentialStore = credentialStore ?? new SessionCredentialStore();
            // null이면 Connect 자체가 비활성화된다 — 개체 탐색기를 읽을 수단이 없으므로
            // 접속할 방법이 아예 없다는 뜻이다. 단위 테스트와 비SSMS 환경이 이 경로다.
            _ssmsConnectionSource = ssmsConnectionSource;
            _gitManager = gitManager ?? new GitManager(_configManager);
            _stateTracker = stateTracker ?? new StateTracker(_configManager, _gitManager, _credentialStore);
            _smoManager = smoManager ?? new SmoManager(_configManager, _credentialStore);
            _notifier = notifier ?? new MessageBoxNotifier();
            _saveDialog = saveDialog ?? new SaveFileDialogAdapter();
            _cleaner = cleaner ?? new WorkingTreeCleaner();
            _folderDialog = folderDialog ?? new FolderBrowserDialogAdapter();
            _scriptExporter = new ScriptExporter(_configManager, _gitManager);
            History = new ObjectHistoryViewModel(_gitManager);

            // 진행 중에는 모두 잠긴다. 같은 추출이 겹쳐 돌면 작업 트리를 동시에 건드리고
            // 나중에 끝난 쪽이 먼저 끝난 쪽의 목록을 덮어쓴다.
            RefreshCommand = new RelayCommand(Refresh, () => !IsBusy);
            SetupCommand = new RelayCommand(Setup, () => !IsBusy);
            CommitCommand = new RelayCommand(Commit, CanCommit);
            ConnectCommand = new RelayCommand(Connect, () => _ssmsConnectionSource != null && !IsBusy);
            ConnectRepositoryCommand = new RelayCommand(ConnectRepository, CanConnectRepository);
            PullCommand = new RelayCommand(Pull, CanPull);
            PushCommand = new RelayCommand(Push, CanPush);
            GenerateDeploymentScriptCommand = new RelayCommand(() => GenerateScript(ScriptKind.Deployment), CanGenerateScript);
            GenerateRollbackScriptCommand = new RelayCommand(() => GenerateScript(ScriptKind.Rollback), CanGenerateScript);
        }

        // ---------- 연결 컨텍스트 ----------

        /// <summary>Connect가 마지막으로 채택한 대상. 입력란이 없으므로 setter는 닫혀 있다.</summary>
        public string? ServerName { get; private set; }

        public string? DatabaseName { get; private set; }

        /// <summary>표시용. 값은 개체 탐색기가 정한다.</summary>
        public SqlAuthMode AuthMode { get; private set; } = SqlAuthMode.Windows;

        /// <summary>표시용. <see cref="SqlAuthMode.Sql"/>일 때만 의미가 있다.</summary>
        public string? UserName { get; private set; }

        private bool HasContext => !string.IsNullOrWhiteSpace(ServerName) && !string.IsNullOrWhiteSpace(DatabaseName);

        /// <summary>
        /// 화면 맨 위에 한 줄로 뜨는 대상 표시.
        ///
        /// "Connect가 마지막으로 채택한 대상"을 말할 뿐 접속 성공 여부는 말하지 않는다 —
        /// 실패는 경고 배너가, 접속되었다는 사실은 변경 목록이 이미 말하고 있어서,
        /// 여기서 같은 것을 반복하면 세 곳을 동시에 맞춰야 한다.
        /// </summary>
        public string TargetSummary
        {
            get
            {
                if (!HasContext)
                {
                    return "(접속되지 않음)";
                }

                var auth = AuthMode == SqlAuthMode.Sql
                    ? $"SQL 인증 ({UserName ?? "계정 미상"})"
                    : "Windows 인증";
                return $"{ServerName}.{DatabaseName} — {auth}";
            }
        }

        /// <summary>
        /// 대상과 인증 정보를 통째로 갈아 끼운다. 네 값은 언제나 함께 온다 —
        /// 개체 탐색기가 하나의 연결에서 읽어 오기 때문이다.
        /// </summary>
        private void SetTarget(string serverName, string databaseName, SqlAuthMode authMode, string? userName)
        {
            ServerName = serverName;
            DatabaseName = databaseName;
            AuthMode = authMode;
            UserName = userName;

            // 대상이 바뀌면 화면이 설명하던 것이 통째로 무효가 된다. 같은 대상으로 다시
            // 누른 경우에도 무효화한다 — 그것은 "지금 상태를 다시 판정해 달라"는 뜻이다.
            InvalidateActiveContext();

            OnPropertyChanged(nameof(ServerName));
            OnPropertyChanged(nameof(DatabaseName));
            OnPropertyChanged(nameof(AuthMode));
            OnPropertyChanged(nameof(UserName));
            OnPropertyChanged(nameof(TargetSummary));
            RaiseActionCanExecuteChanged();
        }

        /// <summary>
        /// 화면이 지금 무엇을 설명하는지를 지운다 — 대상이 바뀔 때 부른다.
        ///
        /// <see cref="Changes"/>·<see cref="IsMapped"/>·<see cref="IsInitialized"/>는 모두 특정
        /// (서버, 데이터베이스) 하나만을 설명하는 값이다. 대상이 바뀌는 순간 이 값들은 더 이상
        /// 화면에 보이는 대상을 가리키지 않는데, 그 사실을 즉시 반영하지 않으면
        /// <see cref="CanCommit"/>은 여전히 참을 반환한다 — A/db1의 변경 목록이 B/db2의 변경
        /// 로그에 처리 완료로 기록되어 버린다.
        /// </summary>
        private void InvalidateActiveContext()
        {
            Changes.Clear();
            SelectedChange = null;
            _lastChangeRecords = new List<ChangeRecord>();
            _failedCleanupPaths.Clear();
            IsMapped = false;
            IsInitialized = false;
            WarningMessage = null;
            // 대상이 바뀌면 "개체 탐색기 선택이 다릅니다"의 판정 근거가 사라진다.
            // 여전히 다르다면 다음 CheckSsmsSelection()에서 다시 뜬다.
            SsmsHintMessage = null;
        }

        // ---------- 개체 탐색기 안내 ----------

        private string? _ssmsHintMessage;

        /// <summary>
        /// 개체 탐색기와 관련해 사용자가 지금 알아야 할 한 줄. 없으면 <c>null</c>이고 UI에서 숨는다.
        ///
        /// 입력란이 사라진 뒤로 이 문장이 "무엇을 해야 하는가"를 말하는 유일한 곳이다.
        /// </summary>
        public string? SsmsHintMessage
        {
            get => _ssmsHintMessage;
            private set
            {
                if (_ssmsHintMessage == value) return;
                _ssmsHintMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSsmsHintMessage));
            }
        }

        public bool HasSsmsHintMessage => !string.IsNullOrEmpty(SsmsHintMessage);

        /// <summary>
        /// 개체 탐색기의 현재 선택이 지금 대상과 다른지 확인하고, 다르면 안내를 띄운다.
        /// <b>대상을 건드리지 않는다</b> — 전환은 사용자가 Connect를 눌러야 일어난다.
        ///
        /// 선택 변경 이벤트를 구독하는 대신, 사용자가 이 패널로 시선을 옮기는 순간
        /// (마우스 진입·포커스)에만 확인한다. 배경 비용이 없고 필요한 시점에만 뜬다.
        /// </summary>
        public void CheckSsmsSelection()
        {
            if (_ssmsConnectionSource == null)
            {
                return;
            }

            var info = _ssmsConnectionSource.TryGetCurrent();

            if (info == null)
            {
                // 대상을 이미 잡아 두었다면 침묵한다 — 개체 탐색기에서 잠깐 다른 노드를 클릭한
                // 것과 구분할 근거가 없고, 그때마다 배너가 뜨면 진짜 경고까지 함께 묻힌다.
                SsmsHintMessage = HasContext
                    ? null
                    : "개체 탐색기에서 데이터베이스(또는 그 하위 개체)를 하나 선택한 뒤 연결을 누르세요.";
                return;
            }

            bool sameTarget =
                string.Equals(info.ServerName, ServerName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(info.DatabaseName, DatabaseName, StringComparison.OrdinalIgnoreCase);

            if (sameTarget)
            {
                SsmsHintMessage = null;
                return;
            }

            SsmsHintMessage = HasContext
                ? $"개체 탐색기 선택이 다릅니다 — {info.ServerName}.{info.DatabaseName}. " +
                  "연결을 누르면 이 대상으로 전환됩니다."
                : $"개체 탐색기 선택: {info.ServerName}.{info.DatabaseName} — 연결을 누르세요.";
        }

        /// <summary>
        /// 개체 탐색기의 현재 선택을 대상으로 채택하고 접속한다. 유일한 연결 경로다.
        /// </summary>
        private void Connect()
        {
            var info = _ssmsConnectionSource?.TryGetCurrent();

            if (info == null)
            {
                // 대상을 모르는 채로 할 수 있는 일이 없다. 다만 지금 대상과 목록은 그대로 둔다 —
                // 읽지 못했다는 사실이 그것들을 거짓으로 만들지는 않는다.
                WarningMessage =
                    "개체 탐색기에서 데이터베이스(또는 그 하위 개체)를 하나 선택한 뒤 다시 누르세요. " +
                    "서버 노드나 여러 개를 한꺼번에 선택한 상태에서는 대상을 정할 수 없습니다.";
                SsmsDiagnostics.Trace("Connect 중단: 개체 탐색기에서 연결을 읽지 못했습니다.");
                return;
            }

            SetTarget(info.ServerName, info.DatabaseName, info.AuthMode, info.UserName);

            if (info.UnsupportedReason != null)
            {
                // 인증 정보를 얻을 길이 없으므로 실패가 확정된 접속을 시도하지 않는다.
                // 시도하면 사유 대신 TestConnection의 낮은 수준 오류가 배너에 뜬다.
                WarningMessage = info.UnsupportedReason;
                SsmsDiagnostics.Trace(
                    $"Connect 중단: {info.ServerName}.{info.DatabaseName} — {info.UnsupportedReason}");
                return;
            }

            _credentialStore.Set(info.ServerName, info.DatabaseName, info.AuthMode, info.UserName, info.Password);
            SsmsDiagnostics.Trace(
                $"접속 시도: {info.ServerName}.{info.DatabaseName} {info.AuthMode} 인증, " +
                $"계정={info.UserName ?? "(없음)"}, 암호 실림={info.Password != null}");

            ApplyContext();
        }

        /// <summary>
        /// 지금 대상에 대해 접속·매핑·초기화 상태를 다시 판정하고 목록을 채운다.
        /// 접속 시도는 응답 없는 서버에서 수십 초까지 걸리므로 UI 스레드에서 하지 않는다.
        /// </summary>
        private void ApplyContext()
        {
            if (!HasContext)
            {
                return;
            }

            var server = ServerName!;
            var database = DatabaseName!;

            IsBusy = true;
            _scheduler.Run(
                () => ProbeContext(server, database),
                ApplyContextProbe,
                ex =>
                {
                    IsBusy = false;
                    _notifier.ShowError("DBVC 연결 실패", ex.Message);
                });
        }

        /// <summary>접속·매핑·초기화 판정. UI에 닿는 것을 건드리지 않는다.</summary>
        private ContextProbe ProbeContext(string server, string database)
        {
            var probe = new ContextProbe
            {
                // 접속부터 확인한다. 실패를 "초기화되지 않음"으로 뭉개면
                // 사용자는 DBVC 초기화 버튼만 보고 원인을 알 수 없다.
                ConnectionError = _stateTracker.TestConnection(server, database),
                IsMapped = _configManager.TryGetMapping(server, database) != null
            };

            // 접속하지 못했으면 초기화 여부는 물어볼 수 없다.
            if (probe.ConnectionError == null)
            {
                probe.IsInitialized = _stateTracker.IsInitialized(server, database);
            }

            return probe;
        }

        private void ApplyContextProbe(ContextProbe probe)
        {
            IsMapped = probe.IsMapped;

            if (probe.ConnectionError != null)
            {
                IsInitialized = false;
                WarningMessage = probe.ConnectionError;
                IsBusy = false;
                return;
            }

            WarningMessage = IsMapped ? null : NotMappedWarning;
            IsInitialized = probe.IsInitialized;

            // Refresh가 스스로 다시 IsBusy를 세우므로 먼저 내려놓는다.
            IsBusy = false;

            if (IsMapped && IsInitialized)
            {
                Refresh();
            }
        }

        private sealed class ContextProbe
        {
            public string? ConnectionError { get; set; }
            public bool IsMapped { get; set; }
            public bool IsInitialized { get; set; }
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

        private bool _isBusy;

        /// <summary>
        /// 백그라운드 작업이 진행 중인지. 진행 중에는 모든 동작 버튼이 잠긴다 —
        /// 같은 추출이 겹쳐 돌면 서로의 결과를 덮어쓰고 작업 트리를 동시에 건드린다.
        /// </summary>
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (_isBusy == value) return;
                _isBusy = value;
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
                History.Load(ServerName, DatabaseName, value?.RelativePath);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>선택된 객체가 바뀌면 뷰가 Diff를 다시 렌더링하도록 알린다.</summary>
        public event EventHandler? SelectionChanged;

        /// <summary>선택된 객체의 커밋 이력. (Feature 7)</summary>
        public ObjectHistoryViewModel History { get; }

        public ICommand RefreshCommand { get; }
        public ICommand SetupCommand { get; }
        public ICommand CommitCommand { get; }

        /// <summary>
        /// 개체 탐색기의 현재 선택을 대상으로 채택하고 접속한다.
        /// 입력란이 없으므로 이것이 유일한 연결 경로다.
        /// </summary>
        public ICommand ConnectCommand { get; }

        /// <summary>
        /// 활성 데이터베이스에 Git 저장소를 매핑한다.
        /// 매핑이 없으면 추출도 커밋도 불가능하므로 여기가 첫 설정 경로다.
        /// </summary>
        public ICommand ConnectRepositoryCommand { get; }

        /// <summary>원격 저장소의 변경을 로컬 저장소로 가져온다. (Feature 6)</summary>
        public ICommand PullCommand { get; }

        /// <summary>로컬 저장소의 커밋을 원격 저장소에 올린다.</summary>
        public ICommand PushCommand { get; }

        /// <summary>선택된 객체들의 현재 DDL을 단일 스크립트로 내보낸다. (Feature 8)</summary>
        public ICommand GenerateDeploymentScriptCommand { get; }

        /// <summary>선택된 객체들의 마지막 커밋 직전 코드를 단일 스크립트로 내보낸다. (Feature 9)</summary>
        public ICommand GenerateRollbackScriptCommand { get; }

        // ---------- Pull ----------

        private bool CanPull() => HasContext && IsMapped;

        private void Pull()
        {
            if (!CanPull()) return;

            var mapping = _configManager.TryGetMapping(ServerName!, DatabaseName!);
            if (mapping == null) return;

            // GetChangedFiles는 미추적 파일도 포함하므로 이 개수가 곧 손실량은 아니다.
            // 문구가 개수를 손실량으로 단정하지 않도록 두 결과를 분리해 알린다.
            var pending = _gitManager.GetChangedFiles(mapping.GitPath);
            if (pending.Count > 0)
            {
                var proceed = _notifier.Confirm(
                    "DBVC Pull",
                    $"커밋하지 않은 변경 {pending.Count}개가 있습니다." + Environment.NewLine +
                    "받아올 변경과 겹치면 Pull이 거부됩니다. 이 경우 저장소는 그대로입니다." + Environment.NewLine +
                    "겹치지 않더라도 병합 중 충돌이 나면 병합을 되돌리면서" + Environment.NewLine +
                    "추적 중인 파일의 변경이 함께 사라질 수 있습니다." + Environment.NewLine +
                    "(DBVC가 추출한 내용은 새로고침으로 다시 만들 수 있습니다)" + Environment.NewLine + Environment.NewLine +
                    "계속하시겠습니까?");

                // 취소는 오류가 아니다.
                if (!proceed) return;
            }

            try
            {
                if (!_gitManager.PullChanges(ServerName!, DatabaseName!))
                {
                    _notifier.ShowError("DBVC Pull 실패", "매핑된 Git 저장소를 찾을 수 없습니다.");
                    return;
                }
            }
            catch (MergeConflictException ex)
            {
                // GitManager가 이미 병합을 되돌렸고 안내 문구도 담고 있다.
                _notifier.ShowError("DBVC Pull 중단", ex.Message);
                return;
            }
            catch (WorkingTreeConflictException ex)
            {
                // 병합이 시작조차 못 했다. 사용자 관점에서 아무 일도 일어나지 않았으므로 '중단'이다.
                _notifier.ShowError("DBVC Pull 중단", ex.Message);
                return;
            }
            catch (Exception ex)
            {
                // 원인이 타입으로 갈렸으므로 흔한 원인을 추측해 덧붙이지 않는다.
                // GitAuthenticationException은 여기서 잡힌다 - Core가 이미 완전한 한국어
                // 안내를 메시지에 담아 던지므로, 전용 catch를 두면 이 분기와 완전히
                // 같은 코드를 중복할 뿐이다. 되살리지 말 것.
                _notifier.ShowError("DBVC Pull 실패", ex.Message);
                return;
            }

            // 여기서 Refresh를 부르면 안 된다. SMO 추출이 방금 받은 원격 변경을 즉시 덮어쓴다.
            _notifier.ShowInfo(
                "DBVC Pull",
                "원격 저장소의 변경을 가져왔습니다." + Environment.NewLine +
                "받은 스크립트를 확인한 뒤 필요하면 데이터베이스에 적용하세요.");

            // History.Load와 SelectionChanged는 Git/작업 트리를 읽기만 할 뿐 SMO를 호출하지 않는다.
            // 그래서 위의 "Refresh 금지" 규칙과 충돌하지 않는다 — 오히려 Pull의 목적(새 커밋 반영)을
            // 이루려면 방금 받은 커밋 로그와 Diff를 화면에 즉시 보여줘야 한다.
            History.Load(ServerName, DatabaseName, SelectedChange?.RelativePath);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        // ---------- Push ----------

        private bool CanPush() => HasContext && IsMapped;

        /// <summary>
        /// Pull과 달리 사전 확인이 없다 - Push는 작업 트리도 커밋 이력도 바꾸지 않으므로
        /// (성공해도 갱신되는 것은 원격 추적 ref뿐이다) 사용자가 잃을 것이 없다.
        /// 성공 후 Refresh나 History 재적재도 하지 않는다. 로컬에 사용자가 보는 것이 바뀐 게 없기 때문이다.
        /// </summary>
        private void Push()
        {
            if (!CanPush()) return;

            PushResult result;
            try
            {
                result = _gitManager.PushChanges(ServerName!, DatabaseName!);
            }
            catch (Exception ex)
            {
                // GitPushRejectedException은 여기서 잡힌다 - Core가 이미 완전한 한국어 안내를
                // 메시지에 담아 던지므로, 전용 catch를 두면 이 분기와 완전히 같은 코드를
                // 중복할 뿐이다. Pull이 GitAuthenticationException에서 겪은 결함이다. 되살리지 말 것.
                _notifier.ShowError("DBVC Push 실패", ex.Message);
                return;
            }

            switch (result)
            {
                case PushResult.NoMapping:
                    _notifier.ShowError("DBVC Push 실패", "매핑된 Git 저장소를 찾을 수 없습니다.");
                    break;
                case PushResult.NothingToPush:
                    _notifier.ShowInfo("DBVC Push", "올릴 커밋이 없습니다. 원격이 이미 최신입니다.");
                    break;
                case PushResult.Pushed:
                    _notifier.ShowInfo("DBVC Push", "커밋을 원격 저장소에 올렸습니다.");
                    break;
            }
        }

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

            // 매핑이 생겼으므로 상태를 다시 판정한다. 인증 정보는 이미 저장소에 있다.
            InvalidateActiveContext();
            ApplyContext();
        }

        // ---------- Setup ----------

        private void Setup()
        {
            if (!HasContext)
            {
                _notifier.ShowError("DBVC", "먼저 개체 탐색기에서 대상 데이터베이스를 선택하세요.");
                return;
            }

            try
            {
                _stateTracker.InitializeDatabase(ServerName!, DatabaseName!);
            }
            catch (Exception ex)
            {
                // 설치 실패(권한 부족 등)를 초기화 성공으로 위장해서는 안 된다.
                _notifier.ShowError("DBVC 초기화 실패", ex.Message);
                return;
            }

            IsInitialized = true;
            Refresh();
        }

        // ---------- Refresh ----------

        public void Refresh()
        {
            Changes.Clear();
            SelectedChange = null;
            _lastChangeRecords = new List<ChangeRecord>();
            _failedCleanupPaths.Clear();
            RaiseActionCanExecuteChanged();

            if (!HasContext) return;

            if (!IsMapped)
            {
                WarningMessage = NotMappedWarning;
                return;
            }

            var server = ServerName!;
            var database = DatabaseName!;

            IsBusy = true;
            _scheduler.Run(
                () => GatherRefresh(server, database),
                ApplyRefreshOutcome,
                ex =>
                {
                    IsBusy = false;
                    WarningMessage = null;
                    _notifier.ShowError("DBVC 새로고침 실패", ex.Message);
                    RaiseActionCanExecuteChanged();
                });
        }

        /// <summary>
        /// 새로고침의 무거운 부분. SMO 추출·변경 로그 조회·Git 상태 읽기·작업 트리 정리를 한다.
        /// UI 스레드 밖에서 돌므로 <see cref="Changes"/>를 비롯한 바인딩 대상을 건드리지 않는다.
        /// </summary>
        private RefreshOutcome GatherRefresh(string server, string database)
        {
            var outcome = new RefreshOutcome();

            // 현재 DB 상태를 파일로 추출해야 Git 상태·Diff가 최신 코드를 반영한다.
            var scriptResult = _smoManager.ScriptObjectsDetailed(server, database, null);
            if (scriptResult == null)
            {
                outcome.Warnings.Add("데이터베이스에서 객체를 추출하지 못했습니다.");
            }
            else if (scriptResult.HasFailures)
            {
                outcome.Warnings.Add($"일부 객체를 추출하지 못했습니다: {string.Join(", ", scriptResult.FailedObjects)}");
            }

            if (!_stateTracker.RefreshState(server, database))
            {
                outcome.Warnings.Add("변경 로그를 읽지 못했습니다.");
            }

            outcome.Records = _stateTracker.GetPendingChanges(server, database);

            // DROP된 객체의 파일을 지워야 Git이 삭제를 감지하고 커밋에 포함할 수 있다.
            // RefreshState가 Git 상태를 읽은 뒤이므로 이 정리가 목록 판정을 바꾸지 않는다.
            var mapping = _configManager.TryGetMapping(server, database);
            if (mapping != null)
            {
                var cleanup = _cleaner.RemoveDeletedObjectFiles(mapping.GitPath, outcome.Records);
                if (cleanup.HasFailures)
                {
                    outcome.Warnings.Add($"삭제된 객체의 파일을 지우지 못했습니다: {string.Join(", ", cleanup.FailedPaths)}");
                    outcome.FailedCleanupPaths.AddRange(cleanup.FailedPaths);
                }
            }

            return outcome;
        }

        private void ApplyRefreshOutcome(RefreshOutcome outcome)
        {
            _lastChangeRecords = outcome.Records;

            foreach (var failedPath in outcome.FailedCleanupPaths)
            {
                _failedCleanupPaths.Add(failedPath);
            }

            foreach (var record in _lastChangeRecords)
            {
                // 정리에 실패한 항목은 체크를 풀어 사용자에게 제외되었음을 보여준다.
                // 파일이 여전히 작업 트리에 남아 있는데 체크된 채면 삭제가 조용히 커밋에서 빠진 것처럼 보인다.
                var cleanupFailed = _failedCleanupPaths.Contains(record.RelativePath);
                Changes.Add(new ChangeItemViewModel
                {
                    ObjectName = record.QualifiedName,
                    State = record.State,
                    RelativePath = record.RelativePath,
                    IsSelected = !cleanupFailed
                });
            }

            WarningMessage = outcome.Warnings.Count > 0 ? string.Join(" / ", outcome.Warnings) : null;
            IsBusy = false;
            RaiseActionCanExecuteChanged();
        }

        private sealed class RefreshOutcome
        {
            public List<string> Warnings { get; } = new List<string>();
            public IReadOnlyList<ChangeRecord> Records { get; set; } = new List<ChangeRecord>();
            public List<string> FailedCleanupPaths { get; } = new List<string>();
        }

        // ---------- Commit ----------

        private bool CanCommit()
        {
            return HasContext
                && IsMapped
                && IsInitialized
                && !IsBusy
                && !string.IsNullOrWhiteSpace(CommitMessage)
                && Changes.Any(c => c.IsSelected);
        }

        /// <summary>
        /// 스테이징은 객체 3000개 기준 15초가 걸린다(libgit2 고유 비용이라 API를 바꿔도 줄지 않는다).
        /// 그래서 커밋도 UI 스레드에서 하지 않는다.
        /// </summary>
        private void Commit()
        {
            if (!CanCommit()) return;

            // 체크박스만으로는 부족하다: 사용자가 정리 실패 경고를 무시하고 다시 체크할 수 있으므로
            // 여기서 한 번 더 걸러야 삭제되지 않은 파일이 커밋·처리 완료로 표시되는 일을 막는다.
            var selected = Changes
                .Where(c => c.IsSelected && !_failedCleanupPaths.Contains(c.RelativePath ?? string.Empty))
                .ToList();
            var selectedPaths = selected
                .Select(c => c.RelativePath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .ToList();

            // 화면 객체를 읽는 일은 전부 여기서 끝낸다. 백그라운드로 넘어가는 것은 값뿐이다.
            var committedNames = new HashSet<string>(
                selected.Select(c => c.ObjectName ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            var committedRecords = _lastChangeRecords.Where(r => committedNames.Contains(r.QualifiedName)).ToList();

            var server = ServerName!;
            var database = DatabaseName!;
            var message = CommitMessage!;

            IsBusy = true;
            _scheduler.Run(
                () =>
                {
                    if (!_gitManager.CommitChanges(server, database, message, selectedPaths)) return false;
                    _stateTracker.MarkProcessed(server, database, committedRecords);
                    return true;
                },
                committed =>
                {
                    IsBusy = false;

                    if (!committed)
                    {
                        WarningMessage = "커밋할 변경사항이 없습니다.";
                        return;
                    }

                    CommitMessage = string.Empty;
                    Refresh();
                },
                ex =>
                {
                    IsBusy = false;
                    _notifier.ShowError("DBVC 커밋 실패", ex.Message);
                });
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

            // 표시용과 파일명용을 가른다. 기본 파일명을 한글로 만들면 폐쇄망 반입이나
            // 다른 도구의 처리에서 인코딩 문제를 살 뿐이고, 얻는 것이 없다.
            var kindText = kind == ScriptKind.Rollback ? "롤백" : "배포";
            var kindSlug = kind == ScriptKind.Rollback ? "Rollback" : "Deployment";
            var title = $"DBVC {kindText} 스크립트";

            var result = _scriptExporter.Export(
                ServerName!, DatabaseName!, GetSelectedRecords(), kind, DateTimeOffset.Now);

            if (!result.HasContent)
            {
                // 오류가 아니다. 내보낼 것이 없다는 사실을 알리고 끝낸다.
                _notifier.ShowInfo(title, WithExclusions("내보낼 내용이 없습니다.", result, kind));
                return;
            }

            var targetPath = _saveDialog.PromptForSavePath(
                $"{title} 저장",
                $"DBVC_{kindSlug}_{DatabaseName}.sql");

            // 사용자가 취소한 경우다. 오류가 아니다.
            if (string.IsNullOrWhiteSpace(targetPath)) return;

            try
            {
                File.WriteAllText(targetPath!, result.Script);
            }
            catch (Exception ex)
            {
                _notifier.ShowError($"{title} 저장 실패", ex.Message);
                return;
            }

            _notifier.ShowInfo(title, WithExclusions($"{result.IncludedCount}개 객체를 내보냈습니다.", result, kind));
        }

        /// <summary>
        /// 제외된 객체가 있으면 사유와 함께 덧붙인다.
        /// 사유가 <see cref="ScriptKind"/>에 따라 다르다 - Rollback은 되돌릴 이전 리비전이 없는 것이고,
        /// Deployment는 작업 트리에 추출된 .sql 파일이 없는 것이다.
        /// </summary>
        private static string WithExclusions(string message, ScriptExportResult result, ScriptKind kind)
        {
            if (result.ExcludedObjects.Count == 0) return message;

            var reason = kind == ScriptKind.Rollback ? "이전 리비전이 없어" : "추출된 파일이 없어";

            return message + Environment.NewLine +
                $"{result.ExcludedObjects.Count}개 객체는 {reason} 제외했습니다: " +
                string.Join(", ", result.ExcludedObjects);
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
            (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SetupCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ConnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CommitCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GenerateDeploymentScriptCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GenerateRollbackScriptCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ConnectRepositoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (PullCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (PushCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

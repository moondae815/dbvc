using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
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

        /// <summary>진행 중인 추출을 멈추기 위한 것. 작업이 없으면 null이다.</summary>
        private CancellationTokenSource? _extractionCancellation;

        /// <summary>
        /// 지금 걸려 있는 작업을 <see cref="Cancel"/>이 실제로 멈출 수 있는지.
        ///
        /// <see cref="IsBusy"/>만으로 취소 버튼을 띄우면 안 된다. Cancel이 취소하는 것은 추출용
        /// 토큰뿐인데 연결·커밋·Pull·Push도 IsBusy를 세운다 — 그때 버튼이 뜨면 눌러도 아무 일이
        /// 없고 "취소하는 중..."만 남는다. 없는 취소를 있는 척하는 버튼보다 없는 편이 정직하다.
        /// </summary>
        private bool _cancellableWorkOutstanding;

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
            RefreshAllCommand = new RelayCommand(RefreshAll, () => !IsBusy);
            CancelCommand = new RelayCommand(Cancel, () => IsBusy && _cancellableWorkOutstanding);
            SetupCommand = new RelayCommand(Setup, () => !IsBusy);
            UpdateTrackerCommand = new RelayCommand(UpdateTracker, () => IsTrackerOutdated && !IsBusy);
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
        /// 설치된 확장의 버전. 상태가 아니라 상수이므로 PropertyChanged를 내지 않는다.
        /// SSMS에서 .vsix를 덮어 설치했을 때 실제로 갱신되었는지 확인할 유일한 자리다.
        /// </summary>
        public string VersionText => "DBVC " + DbvcVersion.Current;

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
            IsTrackerOutdated = false;
            WarningMessage = null;
            // 대상이 바뀌면 이전 대상의 브랜치와 차단 사유가 남아 있으면 안 된다 -
            // 남으면 새 대상의 화면이 엉뚱한 저장소를 근거로 덮인다.
            CurrentBranch = null;
            BlockMessage = null;
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

            // 접속하지 못했으면 설치 버전은 물어볼 수 없다.
            if (probe.ConnectionError == null)
            {
                probe.InstalledVersion = _stateTracker.GetInstalledVersion(server, database);
            }

            // 저장소 상태도 여기서 읽는다. libgit2가 도는 일이라 UI 스레드에서 부르면
            // 개체 탐색기를 붙잡는다 - 이 파일이 접속 판정을 백그라운드로 뺀 이유와 같다.
            // 매핑이 없을 때는 묻지 않는다. 가리킬 저장소가 없고 안내는 이미 따로 뜬다.
            if (probe.IsMapped)
            {
                probe.RepositoryState = _gitManager.GetRepositoryState(server, database);
            }

            return probe;
        }

        private void ApplyContextProbe(ContextProbe probe)
        {
            IsMapped = probe.IsMapped;

            if (probe.ConnectionError != null)
            {
                IsInitialized = false;
                IsTrackerOutdated = false;
                WarningMessage = probe.ConnectionError;
                IsBusy = false;
                return;
            }

            WarningMessage = IsMapped ? null : NotMappedWarning;
            IsInitialized = probe.InstalledVersion > 0;

            // 미설치는 초기화 오버레이가 맡는다. 두 안내를 함께 띄우면 무엇을 눌러야 하는지 흐려진다.
            IsTrackerOutdated = probe.InstalledVersion > 0
                && probe.InstalledVersion < StateTracker.RequiredSchemaVersion;

            CurrentBranch = probe.RepositoryState?.CurrentBranch;
            BlockMessage = probe.RepositoryState?.BlockMessage;

            // Refresh가 스스로 다시 IsBusy를 세우므로 먼저 내려놓는다.
            IsBusy = false;

            if (IsBlocked)
            {
                // 차단 상태에서 Refresh를 돌리면 틀린 기준으로 비교한 목록이 만들어진다.
                Changes.Clear();
                return;
            }

            if (IsMapped && IsInitialized)
            {
                Refresh();
            }
        }

        private sealed class ContextProbe
        {
            public string? ConnectionError { get; set; }
            public bool IsMapped { get; set; }
            public int InstalledVersion { get; set; }

            /// <summary>매핑이 없으면 null이다. 판정은 Core가 하고 여기서는 나르기만 한다.</summary>
            public RepositoryState? RepositoryState { get; set; }
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

        private string? _currentBranch;

        /// <summary>
        /// 저장소의 현재 브랜치. 비교 기준이 브랜치 내용이므로 이것이 보이지 않으면
        /// 사용자가 diff를 오독한다.
        /// </summary>
        public string? CurrentBranch
        {
            get => _currentBranch;
            private set
            {
                if (_currentBranch == value) return;
                _currentBranch = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasCurrentBranch));
            }
        }

        /// <summary>브랜치를 알 수 없으면 표시 자체를 숨긴다. "브랜치: " 만 남으면 오히려 오해를 준다.</summary>
        public bool HasCurrentBranch => !string.IsNullOrWhiteSpace(CurrentBranch);

        private string? _blockMessage;

        /// <summary>
        /// null이 아니면 저장소를 그대로 쓸 수 없다는 뜻이고, 화면을 덮는다.
        /// 경고 배너로 두지 않는 이유는 조용히 틀린 결과가 더 나쁘기 때문이다(설계 3.4).
        /// </summary>
        public string? BlockMessage
        {
            get => _blockMessage;
            private set
            {
                if (_blockMessage == value) return;
                _blockMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBlocked));
                // 이 뷰모델은 CommandManager.RequerySuggested를 구독하지 않으므로
                // 여기서 직접 올리지 않으면 커밋 버튼이 차단된 뒤에도 눌린 채로 남는다.
                RaiseActionCanExecuteChanged();
            }
        }

        public bool IsBlocked => !string.IsNullOrWhiteSpace(BlockMessage);

        private bool _showAllAuthors;

        /// <summary>
        /// 다른 작업자의 변경까지 볼지 여부. 기본은 false다.
        ///
        /// 토글이 필요한 이유가 넷이다 - 커밋하지 않고 떠난 사람의 고아 변경, 휴가 중인 동료의
        /// 대리 커밋, 노트북에서 만들고 데스크톱에서 커밋하는 경우, 그리고 v3 이전의 작업자 없는 행.
        /// </summary>
        public bool ShowAllAuthors
        {
            get => _showAllAuthors;
            set
            {
                if (_showAllAuthors == value) return;
                _showAllAuthors = value;
                OnPropertyChanged();
                Refresh();
            }
        }

        private bool _isTrackerOutdated;

        /// <summary>
        /// 설치된 추적기가 지금 Core가 요구하는 버전보다 낮은지. 참이면 인덱스 변경이 감지되지 않는다.
        /// </summary>
        public bool IsTrackerOutdated
        {
            get => _isTrackerOutdated;
            private set
            {
                if (_isTrackerOutdated == value) return;
                _isTrackerOutdated = value;
                OnPropertyChanged();
                RaiseActionCanExecuteChanged();
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
                OnPropertyChanged(nameof(IsNotBusy));
                RaiseActionCanExecuteChanged();
            }
        }

        /// <summary>
        /// 버튼은 CanExecute가 잠그지만 체크박스에는 명령이 없다. 작업 중 토글이 눌리면
        /// 새로고침이 겹쳐 돌아 서로의 결과를 덮어쓰므로, 화면에서 막을 근거가 필요하다.
        /// </summary>
        public bool IsNotBusy => !IsBusy;

        private string? _progressText;

        /// <summary>진행 표시 옆에 붙는 한 줄. 작업이 없으면 null이다.</summary>
        public string? ProgressText
        {
            get => _progressText;
            private set
            {
                if (_progressText == value) return;
                _progressText = value;
                OnPropertyChanged();
            }
        }

        private void Cancel()
        {
            // Cancel을 눌러도 IsBusy는 작업이 실제로 멈출 때까지 유지된다.
            // 여기서 내리면 사용자가 다른 버튼을 눌러 두 작업이 겹친다.
            _extractionCancellation?.Cancel();
            ProgressText = "취소하는 중...";
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

        /// <summary>DB의 모든 객체를 다시 추출한다.</summary>
        public ICommand RefreshAllCommand { get; }

        /// <summary>진행 중인 추출을 멈춘다. 이미 추출된 파일은 그대로 남는다.</summary>
        public ICommand CancelCommand { get; }
        public ICommand SetupCommand { get; }

        /// <summary>구버전 추적기를 현재 버전으로 다시 설치한다.</summary>
        public ICommand UpdateTrackerCommand { get; }

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

        // !IsBusy가 필요하다. 없으면 추출이 도는 중에도 Pull이 눌려 libgit2 병합과 SMO 추출이
        // 같은 작업 트리를 동시에 건드린다.
        private bool CanPull() => HasContext && IsMapped && !IsBusy;

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

            var server = ServerName!;
            var database = DatabaseName!;
            var gitPath = mapping.GitPath;

            // 네트워크와 ssh 프로세스가 걸리는 구간이다. 원격이 응답하지 않으면 그동안 SSMS
            // 전체가 멈춘다. 확인 대화상자까지는 UI 스레드에 남기고 여기서부터 넘긴다 —
            // 사용자가 취소하면 백그라운드로 나갈 일 자체가 없다.
            IsBusy = true;
            ProgressText = "원격 저장소에서 가져오는 중...";

            _scheduler.Run(
                () => _gitManager.PullChanges(server, database),
                result => ApplyPullResult(result, gitPath),
                ex =>
                {
                    IsBusy = false;
                    ProgressText = null;

                    // 병합이 되돌려졌거나(MergeConflict) 시작조차 못 했다(WorkingTreeConflict).
                    // 어느 쪽이든 사용자가 잃은 것이 없으므로 '실패'가 아니라 '중단'이다.
                    //
                    // 그 밖은 전부 한 갈래다. GitAuthenticationException도 여기서 잡힌다 -
                    // Core가 이미 완전한 한국어 안내를 메시지에 담아 던지므로, 전용 분기를 두면
                    // 완전히 같은 코드를 중복할 뿐이다. 되살리지 말 것.
                    var title = ex is MergeConflictException || ex is WorkingTreeConflictException
                        ? "DBVC Pull 중단"
                        : "DBVC Pull 실패";
                    _notifier.ShowError(title, ex.Message);
                });
        }

        /// <summary>
        /// Pull이 끝난 뒤 화면을 정리한다. 바인딩 대상을 건드리므로 UI 스레드에서만 불린다.
        /// </summary>
        private void ApplyPullResult(PullResult result, string gitPath)
        {
            IsBusy = false;
            ProgressText = null;

            // 여기서 Refresh를 부르면 안 된다. SMO 추출이 방금 받은 원격 변경을 즉시 덮어쓴다.
            switch (result)
            {
                case PullResult.NoMapping:
                    _notifier.ShowError("DBVC Pull 실패", "매핑된 Git 저장소를 찾을 수 없습니다.");
                    return;

                case PullResult.AlreadyUpToDate:
                    // 받은 것이 없으므로 이력도 Diff도 바뀌지 않았다. 아래 재적재를 건너뛴다 -
                    // 화면이 다시 그려지면 사용자는 무언가 받아왔다고 읽는다.
                    _notifier.ShowInfo("DBVC Pull", "원격에 새 변경이 없습니다. 저장소가 이미 최신입니다.");
                    return;

                case PullResult.Pulled:
                    // 받은 스크립트가 어디 놓였는지 말하지 않으면 사용자가 찾지 못한다 -
                    // DBVC는 파일만 가져올 뿐 데이터베이스에 적용하지 않기 때문이다.
                    // 저장소 루트를 알려주는 것만으로는 부족하다 - 실제 파일은 루트가 아니라
                    // ObjectPathConvention이 정한 하위 경로에 있기 때문이다(README.md와 일치시킨다).
                    _notifier.ShowInfo(
                        "DBVC Pull",
                        "원격 저장소의 변경을 가져왔습니다." + Environment.NewLine +
                        "받은 스크립트는 아래 폴더에 있습니다:" + Environment.NewLine + Environment.NewLine +
                        gitPath + Environment.NewLine +
                        "(스크립트는 [스키마]/[객체 유형]/[이름].sql 에 있습니다)" + Environment.NewLine + Environment.NewLine +
                        "확인한 뒤 필요하면 데이터베이스에 적용하세요.");
                    break;

                default:
                    // 클래식 switch문은 열거형이 case를 놓쳐도 컴파일러가 경고하지 않는다.
                    // Pull()에서 처리되지 않은 값은 아래 History 재적재로 그대로 흘러 화면만
                    // 조용히 다시 그려질 뿐 아무 안내도 뜨지 않는다 - 이 지점이 없애려는 혼란
                    // 그 자체이므로, 새 값이 추가되면 여기서 반드시 시끄럽게 죽어야 한다.
                    throw new InvalidOperationException($"처리되지 않은 {nameof(PullResult)}: {result}");
            }

            // History.Load와 SelectionChanged는 Git/작업 트리를 읽기만 할 뿐 SMO를 호출하지 않는다.
            // 그래서 위의 "Refresh 금지" 규칙과 충돌하지 않는다 — 오히려 Pull의 목적(새 커밋 반영)을
            // 이루려면 방금 받은 커밋 로그와 Diff를 화면에 즉시 보여줘야 한다.
            History.Load(ServerName, DatabaseName, SelectedChange?.RelativePath);
            SelectionChanged?.Invoke(this, EventArgs.Empty);

            // 병합 커밋이 만들어지는 Pull은 올릴 커밋을 새로 만든다. RelayCommand는
            // CommandManager.RequerySuggested를 구독하지 않으므로, 여기서 직접 올리지 않으면
            // Push 버튼이 꺼진 채로 남아 사용자가 다른 동작을 할 때까지 그 사실이 드러나지 않는다.
            RaiseActionCanExecuteChanged();
        }

        // ---------- Push ----------

        // !IsBusy를 Git 조회보다 앞에 둔다. 뒤에 두면 작업이 도는 동안에도 CanExecute가
        // 평가될 때마다 저장소를 읽는다.
        private bool CanPush() => HasContext && IsMapped && !IsBusy
                                  && _gitManager.HasCommitsToPush(ServerName!, DatabaseName!);

        /// <summary>
        /// Pull과 달리 사전 확인이 없다 - Push는 작업 트리도 커밋 이력도 바꾸지 않으므로
        /// (성공해도 갱신되는 것은 원격 추적 ref뿐이다) 사용자가 잃을 것이 없다.
        /// 성공 후 Refresh나 History 재적재도 하지 않는다. 로컬에 사용자가 보는 것이 바뀐 게 없기 때문이다.
        /// </summary>
        private void Push()
        {
            if (!CanPush()) return;

            var server = ServerName!;
            var database = DatabaseName!;

            // Pull과 같은 이유로 UI 스레드에서 하지 않는다.
            IsBusy = true;
            ProgressText = "원격 저장소에 올리는 중...";

            _scheduler.Run(
                () => _gitManager.PushChanges(server, database),
                ApplyPushResult,
                ex =>
                {
                    IsBusy = false;
                    ProgressText = null;

                    // GitPushRejectedException은 여기서 잡힌다 - Core가 이미 완전한 한국어 안내를
                    // 메시지에 담아 던지므로, 전용 분기를 두면 완전히 같은 코드를 중복할 뿐이다.
                    // Pull이 GitAuthenticationException에서 겪은 결함이다. 되살리지 말 것.
                    _notifier.ShowError("DBVC Push 실패", ex.Message);
                });
        }

        /// <summary>Push가 끝난 뒤 화면을 정리한다. UI 스레드에서만 불린다.</summary>
        private void ApplyPushResult(PushResult result)
        {
            IsBusy = false;
            ProgressText = null;

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

                default:
                    // Pull()의 default와 같은 이유다 - 컴파일러가 놓친 case를 잡아주지 않으므로
                    // 새 열거값이 추가되면 조용히 지나치지 않고 여기서 드러나야 한다.
                    throw new InvalidOperationException($"처리되지 않은 {nameof(PushResult)}: {result}");
            }

            RaiseActionCanExecuteChanged();
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

            InstallSchema(isUpdate: false);
        }

        /// <summary>구버전 추적기를 다시 설치한다. 스크립트가 멱등이라 초기화와 같은 경로다.</summary>
        private void UpdateTracker()
        {
            if (!HasContext || !IsTrackerOutdated) return;

            InstallSchema(isUpdate: true);
        }

        /// <summary>
        /// 설치 스크립트를 실행한다. DDL 여러 배치를 도는 일이라 응답 없는 서버에서는 수십 초까지
        /// 걸린다 - UI 스레드에 남기면 그동안 SSMS 전체가 멈춘다.
        /// </summary>
        private void InstallSchema(bool isUpdate)
        {
            var server = ServerName!;
            var database = DatabaseName!;

            IsBusy = true;
            ProgressText = isUpdate ? "변경 추적기를 업데이트하는 중..." : "DBVC를 초기화하는 중...";

            _scheduler.Run<object?>(
                () => { _stateTracker.InitializeDatabase(server, database); return null; },
                _ =>
                {
                    IsBusy = false;
                    ProgressText = null;
                    IsInitialized = true;
                    IsTrackerOutdated = false;

                    if (isUpdate)
                    {
                        // 부모를 모르는 옛 인덱스 로그가 이때 닫힌다. 그 변경은 저장소에 반영된 적이
                        // 없을 수 있으므로, 되찾는 유일한 경로를 알려 준다.
                        _notifier.ShowInfo(
                            "DBVC",
                            "변경 추적기를 업데이트했습니다." + Environment.NewLine +
                            "그동안의 인덱스 변경이 저장소에 없을 수 있으니 전체 다시 추출을 한 번 눌러 주세요.");
                        Refresh();
                        return;
                    }

                    // 방금 트리거를 설치했다. 그 이전의 변경은 DDL 로그에 없으므로 전체를 추출해야
                    // 저장소가 DB의 현재 상태를 담는다.
                    RefreshAll();
                },
                ex =>
                {
                    IsBusy = false;
                    ProgressText = null;
                    // 설치 실패(권한 부족 등)를 성공으로 위장해서는 안 된다.
                    _notifier.ShowError(isUpdate ? "DBVC 추적기 업데이트 실패" : "DBVC 초기화 실패", ex.Message);
                });
        }

        // ---------- Refresh ----------

        /// <summary>
        /// DB의 모든 객체를 다시 추출한다.
        ///
        /// DDL 트리거가 없던 동안의 변경, 로그가 잘려 나간 경우, Pull로 받은 파일이 DB와
        /// 어긋난 경우처럼 <c>DBVC_ChangeLog</c>가 모르는 차이를 되찾을 수 있는 유일한 경로다.
        /// 느리므로 기본 새로고침은 이것을 하지 않는다.
        /// </summary>
        public void RefreshAll()
        {
            Refresh(fullExtraction: true);
        }

        /// <summary>DDL 로그가 가리키는 객체만 다시 추출한다.</summary>
        public void Refresh()
        {
            Refresh(fullExtraction: false);
        }

        private void Refresh(bool fullExtraction)
        {
            Changes.Clear();
            SelectedChange = null;
            _lastChangeRecords = new List<ChangeRecord>();
            _failedCleanupPaths.Clear();
            RaiseActionCanExecuteChanged();

            // 이력을 여기서 직접 읽는다. 위의 대입에 기대면 안 된다 - SelectedChange가 이미 null이면
            // setter가 ReferenceEquals로 조기 반환해 이력이 갱신되지 않는다. 커밋 직후가 정확히
            // 그 경우다(목록에서 객체를 고른 적이 없으면 계속 null이다). 그래서 첫 커밋을 하고도
            // 이력 탭이 비어 있었다. Git만 읽으므로 아래 SMO 추출을 기다리지 않고 바로 뜬다.
            History.Load(ServerName, DatabaseName, null);

            if (!HasContext) return;

            if (!IsMapped)
            {
                WarningMessage = NotMappedWarning;
                return;
            }

            var server = ServerName!;
            var database = DatabaseName!;
            // UI 스레드에서 읽어 값으로 넘긴다. 백그라운드에서 바인딩 속성을 읽지 않는 규약이다.
            var includeAllAuthors = ShowAllAuthors;

            _extractionCancellation?.Dispose();
            _extractionCancellation = new CancellationTokenSource();
            var token = _extractionCancellation.Token;

            _cancellableWorkOutstanding = true;
            IsBusy = true;
            ProgressText = "시작하는 중...";

            _scheduler.Run(
                () => GatherRefresh(server, database, fullExtraction, includeAllAuthors, token),
                ApplyRefreshOutcome,
                ex =>
                {
                    _cancellableWorkOutstanding = false;
                    IsBusy = false;
                    ProgressText = null;
                    RaiseActionCanExecuteChanged();

                    // 취소는 실패가 아니다. 오류 상자로 알리면 사용자가 자기가 누른 것을
                    // 오류로 되읽는다. 이미 추출된 파일은 남아 있으므로 목록도 지우지 않는다.
                    if (ex is OperationCanceledException)
                    {
                        WarningMessage = "추출을 취소했습니다. 여기까지 추출된 내용은 저장소에 남아 있습니다.";
                        return;
                    }

                    WarningMessage = null;
                    _notifier.ShowError("DBVC 새로고침 실패", ex.Message);
                });
        }

        /// <summary>
        /// 새로고침의 무거운 부분. SMO 추출·변경 로그 조회·Git 상태 읽기·작업 트리 정리를 한다.
        /// UI 스레드 밖에서 돌므로 <see cref="Changes"/>를 비롯한 바인딩 대상을 건드리지 않는다.
        /// </summary>
        private RefreshOutcome GatherRefresh(string server, string database, bool fullExtraction, bool includeAllAuthors, CancellationToken cancellationToken)
        {
            var outcome = new RefreshOutcome();
            var mapping = _configManager.TryGetMapping(server, database);

            // 현재 DB 상태를 파일로 추출해야 Git 상태·Diff가 최신 코드를 반영한다.
            Extract(server, database, mapping, fullExtraction, outcome, cancellationToken);

            if (!_stateTracker.RefreshState(server, database, includeAllAuthors))
            {
                outcome.Warnings.Add("변경 로그를 읽지 못했습니다.");
            }

            outcome.Records = _stateTracker.GetPendingChanges(server, database);

            // DROP된 객체의 파일을 지워야 Git이 삭제를 감지하고 커밋에 포함할 수 있다.
            // RefreshState가 Git 상태를 읽은 뒤이므로 이 정리가 목록 판정을 바꾸지 않는다.
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

        /// <summary>
        /// 무엇을 추출할지 정하고 추출한다.
        ///
        /// 기본은 DDL 로그가 가리키는 객체만이다. 전체 추출은 SMO 왕복이 객체 수에 비례해
        /// 쌓여 1000개짜리 DB에서 수 분이 걸린다(CPU가 아니라 SQL 대기다).
        ///
        /// 다만 기준선이 없으면 전체를 추출해야 한다. 나머지 객체의 파일이 저장소에 없는데
        /// 변경분만 뽑으면 사용자는 커밋할 것을 찾지 못한다.
        /// </summary>
        private void Extract(string server, string database, MappingConfig? mapping, bool fullExtraction, RefreshOutcome outcome, CancellationToken cancellationToken)
        {
            var extractAll = fullExtraction || mapping == null || !ExtractionBaseline.Exists(mapping.GitPath);

            List<string>? targets = null;
            if (!extractAll)
            {
                targets = _stateTracker.GetChangedObjectNames(server, database)?.ToList() ?? new List<string>();

                // 빈 목록을 그대로 넘기면 SmoManager가 "필터 없음"으로 읽어 전체를 추출한다.
                // 로그가 비어 있다는 것은 추출할 것이 없다는 뜻이므로 아예 부르지 않는다.
                if (targets.Count == 0) return;
            }

            // 보고는 백그라운드 스레드에서 온다. 바인딩 속성은 UI 스레드에서만 바꾼다.
            var progress = new ExtractionProgressRelay(p =>
            {
                var text = $"{p.Completed}/{p.Total} 추출 중 — {p.CurrentObject}";
                _scheduler.Post(() => ProgressText = text);
            });

            var scriptResult = _smoManager.ScriptObjectsDetailed(server, database, targets, progress, cancellationToken);

            if (scriptResult == null)
            {
                outcome.Warnings.Add("데이터베이스에서 객체를 추출하지 못했습니다.");
            }
            else if (scriptResult.HasFailures)
            {
                outcome.Warnings.Add($"일부 객체를 추출하지 못했습니다: {string.Join(", ", scriptResult.FailedObjects)}");
            }
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
                    ObjectType = record.ObjectType,
                    State = record.State,
                    RelativePath = record.RelativePath,
                    // 공용 계정 환경에서는 로그인 이름이 전부 같으므로 접속 PC를 우선한다.
                    Author = string.IsNullOrWhiteSpace(record.HostName) ? record.Author : record.HostName,
                    IsSelected = !cleanupFailed
                });
            }

            WarningMessage = outcome.Warnings.Count > 0 ? string.Join(" / ", outcome.Warnings) : null;
            _cancellableWorkOutstanding = false;
            ProgressText = null;
            IsBusy = false;
            RaiseActionCanExecuteChanged();
        }

        /// <summary>
        /// 보고를 그 자리에서 전달한다. <see cref="Progress{T}"/>는 생성된 스레드의
        /// SynchronizationContext로 넘기는데, 백그라운드 스레드에는 그것이 없어 보고가
        /// 스레드 풀로 흩어지고 순서가 뒤집힌다.
        /// </summary>
        private sealed class ExtractionProgressRelay : IProgress<ExtractionProgress>
        {
            private readonly Action<ExtractionProgress> _onReport;
            public ExtractionProgressRelay(Action<ExtractionProgress> onReport) { _onReport = onReport; }
            public void Report(ExtractionProgress value) => _onReport(value);
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
            // 차단은 경고가 아니다. 조용히 틀린 결과를 내는 것보다 멈추는 편이 낫다.
            if (IsBlocked) return false;

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
        private void Commit() => Commit(coAuthorConfirmed: false);

        /// <param name="coAuthorConfirmed">
        /// 사용자가 이미 "남의 변경이 딸려 온다"는 확인에 동의했는지. 확인 대화상자는 UI
        /// 스레드에서만 띄울 수 있는데 판정은 DB를 읽어야 해서, 판정을 백그라운드에서 마치고
        /// 확인을 받은 뒤 이 값을 참으로 해서 같은 경로를 다시 탄다.
        /// </param>
        private void Commit(bool coAuthorConfirmed)
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
            _scheduler.Run<CommitOutcome>(
                () =>
                {
                    // 커밋 직전에 본다. 목록을 만든 시점과 커밋 시점 사이에 남이 또 만졌을 수 있다.
                    // 조회가 DB를 읽으므로 UI 스레드가 아니라 여기서 한다.
                    if (!coAuthorConfirmed)
                    {
                        var warnings = _stateTracker.GetCoAuthorWarnings(server, database, committedNames);
                        if (warnings != null && warnings.Count > 0)
                        {
                            return new CommitOutcome { CoAuthors = warnings };
                        }
                    }

                    if (!_gitManager.CommitChanges(server, database, message, selectedPaths))
                    {
                        return new CommitOutcome();
                    }

                    _stateTracker.MarkProcessed(server, database, committedRecords);
                    return new CommitOutcome { Committed = true };
                },
                outcome =>
                {
                    IsBusy = false;

                    if (outcome.CoAuthors != null)
                    {
                        // 차단이 아니라 확인이다. 대부분은 실제로 이어서 작업한 정상적인 경우이고,
                        // 막으면 사람들이 도구를 쓰지 않게 된다(설계 3.10).
                        if (AskToCommitWithOtherAuthorsWork(outcome.CoAuthors))
                        {
                            Commit(coAuthorConfirmed: true);
                        }

                        return;
                    }

                    if (!outcome.Committed)
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

        /// <summary>
        /// 커밋 결과. bool로는 "커밋됨 / 커밋할 것 없음 / 확인이 필요함" 셋을 구분할 수 없다.
        /// </summary>
        private sealed class CommitOutcome
        {
            public bool Committed { get; set; }

            /// <summary>null이 아니면 커밋하지 않았고 사용자 확인이 필요하다는 뜻이다.</summary>
            public IReadOnlyList<CoAuthorWarning>? CoAuthors { get; set; }
        }

        private bool AskToCommitWithOtherAuthorsWork(IReadOnlyList<CoAuthorWarning> coAuthors)
        {
            var lines = string.Join(Environment.NewLine,
                coAuthors.Select(w => $"  · {w.QualifiedName} — {w.Author}"));

            return _notifier.Confirm(
                "DBVC 커밋 확인",
                "다음 객체는 다른 작업자도 변경했습니다. 지금 커밋하는 내용에 그 변경이 포함됩니다."
                + Environment.NewLine + Environment.NewLine + lines
                + Environment.NewLine + Environment.NewLine + "그대로 커밋할까요?");
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
            (RefreshAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SetupCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (UpdateTrackerCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

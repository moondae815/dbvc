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
            ISsmsConnectionSource? ssmsConnectionSource = null)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _credentialStore = credentialStore ?? new SessionCredentialStore();
            // null이면 자동 채움이 꺼진 것과 같다. 단위 테스트와 비SSMS 환경이 이 경로다.
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

            RefreshCommand = new RelayCommand(Refresh);
            SetupCommand = new RelayCommand(Setup);
            CommitCommand = new RelayCommand(Commit, CanCommit);
            ConnectCommand = new RelayCommand(() => SetContext(ServerName, DatabaseName), () => HasContext);
            ConnectRepositoryCommand = new RelayCommand(ConnectRepository, CanConnectRepository);
            RefreshFromSsmsCommand = new RelayCommand(() => TryFillFromSsms(), () => _ssmsConnectionSource != null);
            PullCommand = new RelayCommand(Pull, CanPull);
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
                ForgetSsmsPassword();
                InvalidateActiveContext();
                _serverName = value;
                OnPropertyChanged();
                RaiseConnectCanExecuteChanged();
                LoadSavedCredential();
            }
        }

        private string? _databaseName;
        public string? DatabaseName
        {
            get => _databaseName;
            set
            {
                if (_databaseName == value) return;
                ForgetSsmsPassword();
                InvalidateActiveContext();
                _databaseName = value;
                OnPropertyChanged();
                RaiseConnectCanExecuteChanged();
                LoadSavedCredential();
            }
        }

        private bool HasContext => !string.IsNullOrWhiteSpace(ServerName) && !string.IsNullOrWhiteSpace(DatabaseName);

        /// <summary>
        /// 화면이 지금 무엇을 설명하는지를 지운다 — 대상(서버·데이터베이스)이 바뀔 때 부른다.
        ///
        /// <see cref="Changes"/>·<see cref="IsMapped"/>·<see cref="IsInitialized"/>는 모두 특정
        /// (서버, 데이터베이스) 하나만을 설명하는 값이다. ServerName이나 DatabaseName이 바뀌는
        /// 순간 이 값들은 더 이상 화면에 보이는 대상을 가리키지 않는데, 그 사실을 이 메서드가
        /// 즉시 반영하지 않으면 <see cref="CanCommit"/>은 여전히 참을 반환한다 — 예를 들어
        /// A/db1에 접속해 목록을 채운 뒤 SSMS 개체 탐색기 포커스가 B/db2로 옮겨가면(가시성
        /// 트리거가 ServerName/DatabaseName만 조용히 재대입한다), 사용자가 Commit을 누를 때
        /// A/db1의 변경 목록이 B/db2의 변경 로그에 처리 완료로 기록되어 버린다.
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
            // 대상이 바뀌면 "개체 탐색기 선택이 다릅니다"의 판정 근거가 사라진다. 남겨 두면
            // 사용자가 직접 서버를 개체 탐색기와 같게 고쳐도 배너가 계속 다르다고 우긴다.
            // 여전히 다르다면 다음 CheckSsmsSelection()에서 다시 뜬다.
            SsmsHintMessage = null;
        }

        // ---------- 인증 ----------

        private SqlAuthMode _authMode = SqlAuthMode.Windows;

        /// <summary>
        /// 접속에 쓸 인증 방식. Connect 시점에 <see cref="ISqlCredentialStore"/>에 저장된다.
        /// </summary>
        public SqlAuthMode AuthMode
        {
            get => _authMode;
            set
            {
                if (_authMode == value) return;
                ForgetSsmsPassword();
                _authMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSqlAuth));
            }
        }

        /// <summary>SQL 인증일 때만 사용자명·암호 입력란을 보인다.</summary>
        public bool IsSqlAuth => AuthMode == SqlAuthMode.Sql;

        /// <summary>인증 방식 콤보의 항목. 열거형을 그대로 노출하면 영문 이름이 보인다.</summary>
        public IReadOnlyList<AuthModeOption> AuthModes { get; } = new[]
        {
            new AuthModeOption(SqlAuthMode.Windows, "Windows 인증"),
            new AuthModeOption(SqlAuthMode.Sql, "SQL Server 인증")
        };

        public class AuthModeOption
        {
            public AuthModeOption(SqlAuthMode mode, string label)
            {
                Mode = mode;
                Label = label;
            }

            public SqlAuthMode Mode { get; }
            public string Label { get; }
        }

        private string? _userName;
        public string? UserName
        {
            get => _userName;
            set
            {
                if (_userName == value) return;
                ForgetSsmsPassword();
                _userName = value;
                OnPropertyChanged();
            }
        }

        private string? _password;

        /// <summary>
        /// 입력 중인 평문 암호. Connect가 끝나면 즉시 비운다 — 보관은 저장소가 하고,
        /// ViewModel이 세션 내내 평문을 들고 있을 이유가 없다.
        ///
        /// <c>null</c>이거나 비어 있으면 "저장된 암호를 그대로 쓴다"는 뜻이다.
        /// PasswordBox는 바인딩을 지원하지 않으므로 코드 비하인드가 이 속성에 밀어 넣는다.
        ///
        /// setter를 탄다는 것은 곧 사용자가 직접 입력했다는 뜻이므로 출처 표시를 내린다.
        /// SSMS에서 가져온 암호는 이 setter를 거치지 않고 <see cref="TryFillFromSsms"/>가
        /// 백킹 필드에 직접 넣는다 — 그래야 저장 경로가 갈린다.
        /// </summary>
        public string? Password
        {
            get => _password;
            set
            {
                _password = value;
                PasswordFromSsms = false;
            }
        }

        private bool _passwordFromSsms;

        /// <summary>
        /// 현재 들고 있는 암호가 SSMS에서 온 것인지. 참이면 디스크에 저장하지 않는다.
        ///
        /// 필드가 아니라 속성인 이유는 UI가 이 상태를 봐야 하기 때문이다
        /// (<see cref="HasSsmsPassword"/>). 대입 지점이 네 곳으로 흩어져 있어, 각 지점에서
        /// 알림을 따로 올리게 두면 언젠가 하나가 빠진다.
        /// </summary>
        private bool PasswordFromSsms
        {
            get => _passwordFromSsms;
            set
            {
                if (_passwordFromSsms == value) return;
                _passwordFromSsms = value;
                OnPropertyChanged(nameof(HasSsmsPassword));
            }
        }

        /// <summary>
        /// 암호 칸은 비어 있는데 SSMS에서 가져온 암호가 실려 있는 상태인지.
        ///
        /// UI는 이 값이 참일 때 암호 칸 위에 표시 전용 마스킹을 덮는다. 실제 문자를
        /// PasswordBox에 넣지 않는 것은 <see cref="Password"/> setter를 타는 순간 출처 표시가
        /// 풀려 그 암호가 디스크로 새어 나가기 때문이다 — 이번 기능의 핵심 계약과 정반대다.
        /// </summary>
        public bool HasSsmsPassword => _passwordFromSsms;

        /// <summary>
        /// SSMS에서 가져온 암호를 버린다.
        ///
        /// 그 암호는 가져올 당시의 (서버, 데이터베이스, 인증 방식, 계정)에만 속한다. 넷 중 하나라도
        /// 바뀌면 더 이상 이 암호가 맞는 대상이 아니므로 들고 있어서는 안 된다 — 들고 있으면
        /// Connect가 다른 서버로 그 암호를 보내는 접속을 시도한다.
        ///
        /// 사용자가 직접 입력한 암호는 건드리지 않는다. 대상을 고치는 도중에 입력값이 사라지면
        /// 그쪽이 결함이다.
        /// </summary>
        private void ForgetSsmsPassword()
        {
            if (!PasswordFromSsms) return;

            _password = null;
            PasswordFromSsms = false;
            ConnectionSourceMessage = null;
        }

        private string? _connectionSourceMessage;

        /// <summary>
        /// 자동 채움이 무슨 일을 했는지 알리는 한 줄. 채운 적이 없으면 <c>null</c>이고 UI에서 숨는다.
        ///
        /// 필요한 이유: SSMS에서 가져온 암호는 PasswordBox에 넣지 않으므로(넣으면 Password setter를
        /// 타서 디스크 저장 경로로 새어 나간다) 암호 칸은 비어 있는데 암호는 실려 있는 상태가 된다.
        /// 그 사실을 알리지 않으면 사용자가 다시 입력해야 하는 줄 안다.
        /// </summary>
        public string? ConnectionSourceMessage
        {
            get => _connectionSourceMessage;
            private set
            {
                if (_connectionSourceMessage == value) return;
                _connectionSourceMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasConnectionSourceMessage));
            }
        }

        public bool HasConnectionSourceMessage => !string.IsNullOrEmpty(ConnectionSourceMessage);

        private string? _ssmsHintMessage;

        /// <summary>
        /// 개체 탐색기와 관련해 사용자가 지금 알아야 할 한 줄. 없으면 <c>null</c>이고 UI에서 숨는다.
        ///
        /// <see cref="ConnectionSourceMessage"/>와 나눠 둔 이유가 있다. 그쪽은 "지금 입력란에 있는
        /// 값이 어디서 왔는가"를 말하는 사후 보고이고, 이쪽은 "무언가 하려면 무엇을 눌러야
        /// 하는가"를 말하는 행동 안내다. 두 문장은 동시에 참일 수 있다 — SSMS에서 가져온 값을
        /// 들고 있는 채로 개체 탐색기 선택만 다른 곳으로 옮겨 간 상태가 그렇다.
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
        /// 이 세션에서 자동 채움이 한 번이라도 성공했는지.
        ///
        /// <see cref="CheckSsmsSelection"/>의 전제다. 개체 탐색기를 쓰지 않고 직접 입력만 하는
        /// 사용자에게는 "선택이 다릅니다"가 참이면서도 아무 의미가 없다 — 패널을 볼 때마다
        /// 뜨는 배너는 읽히지 않고 진짜 경고까지 같이 묻히게 만든다.
        /// </summary>
        private bool _ssmsFillEverSucceeded;

        /// <summary>
        /// 개체 탐색기의 현재 선택이 입력란과 다른지 확인하고, 다르면 안내를 띄운다.
        /// <b>입력란을 건드리지 않는다</b> — 갱신은 사용자가 버튼을 눌러야 일어난다.
        ///
        /// 개체 탐색기의 선택 변경 이벤트를 구독하는 대신, 사용자가 이 패널로 시선을 옮기는
        /// 순간(마우스 진입·포커스)에만 확인한다. 배경 비용이 없고, 안내가 필요한 바로 그
        /// 시점에만 뜬다.
        /// </summary>
        public void CheckSsmsSelection()
        {
            if (!_ssmsFillEverSucceeded || _ssmsConnectionSource == null)
            {
                return;
            }

            var info = _ssmsConnectionSource.TryGetCurrent();
            if (info == null)
            {
                // 고를 수 없는 선택(선택 없음·다중 선택·서버 노드)은 "달라졌다"고 말할 근거가
                // 아니다. 개체 탐색기에서 잠깐 다른 것을 클릭했다고 배너가 뜨면 안 된다.
                SsmsHintMessage = null;
                return;
            }

            bool sameTarget =
                string.Equals(info.ServerName, ServerName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(info.DatabaseName, DatabaseName, StringComparison.OrdinalIgnoreCase);

            SsmsHintMessage = sameTarget
                ? null
                : $"개체 탐색기 선택이 다릅니다 — {info.ServerName}.{info.DatabaseName}. " +
                  "[개체 탐색기에서 가져오기]를 누르면 이 값으로 갱신됩니다.";
        }

        /// <summary>
        /// 대상이 정해지면 저장된 인증 정보를 입력란에 되살린다.
        ///
        /// Server/Database setter에서 호출한다. 이게 없으면 SSMS를 재시작했을 때 콤보가
        /// 기본값(Windows 인증)인 채로 Connect가 눌리고, <see cref="PersistCredential"/>이
        /// 저장해 둔 SQL 인증을 Windows 인증으로 덮어써 버린다.
        /// </summary>
        public void LoadSavedCredential()
        {
            if (!HasContext) return;

            var credential = _credentialStore.TryGet(ServerName!, DatabaseName!);
            if (credential == null) return;

            AuthMode = credential.AuthMode;
            UserName = credential.UserName;
        }

        /// <summary>
        /// SSMS 개체 탐색기의 현재 연결을 입력란에 채운다. 접속하지는 않는다 — 확정은 Connect가 한다.
        /// </summary>
        /// <returns>채웠으면 true. 가져올 연결이 없거나 채우지 않기로 했으면 false.</returns>
        public bool TryFillFromSsms()
        {
            if (_ssmsConnectionSource == null)
            {
                SsmsDiagnostics.Trace("자동 채움 중단: 연결 소스가 주입되지 않았습니다.");
                return false;
            }

            var info = _ssmsConnectionSource.TryGetCurrent();
            if (info == null) return false;   // 사유는 소스가 이미 남겼다.

            // 사용자가 입력 중인 암호를 지우지 않는다. 도구 창이 다시 보일 때마다 이 메서드가
            // 불리므로(가시성 트리거), 가드가 없으면 타이핑 중이던 값이 사라진다.
            if (!PasswordFromSsms && !string.IsNullOrEmpty(_password))
            {
                // 버튼을 눌러도 아무 일이 없는 것처럼 보이는 경우가 여기다. 사용자에게는
                // 버튼이 고장 난 것과 구분되지 않으므로, 진단만 남기지 말고 화면에도 알린다.
                SsmsDiagnostics.Trace("자동 채움 건너뜀: 사용자가 입력한 암호가 남아 있습니다.");
                SsmsHintMessage =
                    "암호 칸에 입력한 값이 있어 가져오지 않았습니다. 암호 칸을 비우고 다시 누르세요.";
                return false;
            }

            // 순서가 계약이다. Server/Database setter가 LoadSavedCredential()을 호출해
            // AuthMode·UserName을 저장소 값으로 되돌리므로, SSMS 값은 반드시 그 뒤에 얹는다.
            ServerName = info.ServerName;
            DatabaseName = info.DatabaseName;

            // 입력란이 개체 탐색기와 같아졌으므로 "선택이 다릅니다"는 더 이상 참이 아니다.
            // 여기서 내리지 않으면 방금 누른 버튼이 배너를 남긴 것처럼 보인다.
            _ssmsFillEverSucceeded = true;
            SsmsHintMessage = null;

            if (info.UnsupportedReason != null)
            {
                // 서버·DB는 쓸 수 있지만 인증은 사용자가 직접 지정해야 한다.
                // 이미 입력해 둔 인증 정보를 지우지 않는다.
                //
                // 대상이 바뀌었다면 위의 ServerName/DatabaseName setter가 ForgetSsmsPassword()로
                // 암호까지 이미 정리했다. 대상이 그대로인데 지원 여부만 바뀐 경우(setter가
                // 호출되지 않아 ForgetSsmsPassword가 돌지 않는 경우)에도 배너만은 여기서 내린다 —
                // 이 경우는 인증 정보(AuthMode·UserName)도 그대로이므로 암호까지 버릴 필요는 없다.
                ConnectionSourceMessage = null;
                WarningMessage = info.UnsupportedReason;
                return true;
            }

            AuthMode = info.AuthMode;
            UserName = info.UserName;
            _password = info.Password;
            PasswordFromSsms = info.Password != null;

            ConnectionSourceMessage = PasswordFromSsms
                ? "SSMS 개체 탐색기 연결에서 가져왔습니다 (암호 포함). Connect를 누르세요."
                : "SSMS 개체 탐색기 연결에서 가져왔습니다. Connect를 누르세요.";

            // 채웠다는 기록은 채운 쪽이 남긴다. 어댑터는 읽기만 하고, 그 읽기는 대조 목적으로도
            // 일어나므로 거기서 "채웠다"고 적으면 로그가 일어나지 않은 일을 보고하게 된다.
            SsmsDiagnostics.Trace(
                $"자동 채움: {ServerName}.{DatabaseName} {AuthMode} 인증, " +
                $"계정={UserName ?? "(없음)"}, 암호 실림={PasswordFromSsms}");
            return true;
        }

        /// <summary>
        /// 활성 데이터베이스를 지정하고 인증 정보·매핑·초기화 상태를 다시 판정한다.
        /// </summary>
        public void SetContext(string? serverName, string? databaseName)
        {
            ServerName = serverName;
            DatabaseName = databaseName;

            // ServerName/DatabaseName setter는 값이 실제로 바뀔 때만 InvalidateActiveContext()를
            // 호출한다(동일값 재대입은 early-return한다). ConnectCommand는 매번 SetContext(ServerName,
            // DatabaseName)을 그대로 호출하므로 — 같은 대상으로 재접속하는 흔한 경로다 — 여기서
            // 한 번 더 무효화해야 아래에서 판정하는 최신 상태로 다시 채워진다.
            InvalidateActiveContext();

            if (!HasContext)
            {
                return;
            }

            PersistCredential();

            // 접속부터 확인한다. 실패를 "초기화되지 않음"으로 뭉개면
            // 사용자는 Setup DBVC 버튼만 보고 원인을 알 수 없다.
            var connectionError = _stateTracker.TestConnection(ServerName!, DatabaseName!);
            if (connectionError != null)
            {
                IsMapped = _configManager.TryGetMapping(ServerName!, DatabaseName!) != null;
                IsInitialized = false;
                WarningMessage = connectionError;
                return;
            }

            IsMapped = _configManager.TryGetMapping(ServerName!, DatabaseName!) != null;
            WarningMessage = IsMapped ? null : NotMappedWarning;

            IsInitialized = _stateTracker.IsInitialized(ServerName!, DatabaseName!);

            if (IsMapped && IsInitialized)
            {
                Refresh();
            }
        }

        /// <summary>
        /// 입력·수집된 인증 정보를 저장소에 반영한다.
        ///
        /// 반환값이 없어졌다. 디스크 쓰기가 사라지면서 "저장에 실패해 접속할 수 없다"는 상태
        /// 자체가 없어졌기 때문이다 — 메모리 사전 대입은 실패하지 않는다.
        /// </summary>
        private void PersistCredential()
        {
            try
            {
                _credentialStore.Set(ServerName!, DatabaseName!, AuthMode, UserName, _password);
                SsmsDiagnostics.Trace(
                    $"인증 정보 반영: {ServerName}.{DatabaseName} {AuthMode} 인증, " +
                    $"계정={UserName ?? "(없음)"}, 암호 실림={!string.IsNullOrEmpty(_password)}");
            }
            finally
            {
                // 평문을 ViewModel이 세션 내내 들고 있을 이유가 없다. 저장소가 들고 있다.
                _password = null;
                PasswordFromSsms = false;
                ConnectionSourceMessage = null;
            }
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
        /// 입력된 서버/데이터베이스를 활성 컨텍스트로 적용한다.
        /// 입력란은 사용자가 직접 채우거나 <see cref="RefreshFromSsmsCommand"/>가 채운다.
        /// </summary>
        public ICommand ConnectCommand { get; }

        /// <summary>
        /// SSMS 개체 탐색기의 현재 연결을 입력란으로 가져온다.
        /// 도구 창을 개체 탐색기와 나란히 띄워 두면 가시성 이벤트가 뜨지 않으므로,
        /// 결정적인 수동 갱신 수단이 하나 필요하다.
        /// </summary>
        public ICommand RefreshFromSsmsCommand { get; }

        /// <summary>
        /// 활성 데이터베이스에 Git 저장소를 매핑한다.
        /// 매핑이 없으면 추출도 커밋도 불가능하므로 여기가 첫 설정 경로다.
        /// </summary>
        public ICommand ConnectRepositoryCommand { get; }

        /// <summary>원격 저장소의 변경을 로컬 저장소로 가져온다. (Feature 6)</summary>
        public ICommand PullCommand { get; }

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
                    "(DBVC가 추출한 내용은 Refresh로 다시 만들 수 있습니다)" + Environment.NewLine + Environment.NewLine +
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
                _stateTracker.InitializeDatabase(ServerName!, DatabaseName!);
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
                        foreach (var failedPath in cleanup.FailedPaths)
                        {
                            _failedCleanupPaths.Add(failedPath);
                        }
                    }
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

            var kindLabel = kind == ScriptKind.Rollback ? "Rollback" : "Deployment";
            var title = $"DBVC {kindLabel} Script";

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
                $"DBVC_{kindLabel}_{DatabaseName}.sql");

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
            (CommitCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GenerateDeploymentScriptCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GenerateRollbackScriptCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ConnectRepositoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (PullCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

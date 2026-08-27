using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
    /// 배포·감사 대상의 차이 검사와 배포 스크립트 생성.
    ///
    /// ViewChangesViewModel에 얹지 않는 이유는 그쪽이 이미 1592줄이고 대상 선택·접속·매핑·
    /// 차단·커밋·이력을 전부 들고 있기 때문이다. 진행 표시와 취소만 <see cref="BusyState"/>로
    /// 공유한다 — 도구 줄에 그것이 둘 생기면 사용자가 무엇이 도는지 알 수 없다.
    /// </summary>
    public class DeploymentViewModel : INotifyPropertyChanged
    {
        private readonly IConfigManager _configManager;
        private readonly IGitManager _gitManager;
        private readonly ISmoManager _smoManager;
        private readonly ScriptExporter _scriptExporter;
        private readonly IUserNotifier _notifier;
        private readonly IFileSaveDialog _saveDialog;
        private readonly IBackgroundScheduler _scheduler;

        private CancellationTokenSource? _comparison;
        private ComparisonResult? _lastResult;
        private string? _serverName;
        private string? _databaseName;
        private MappingMode _mode = MappingMode.Write;

        public DeploymentViewModel(
            IConfigManager configManager,
            IGitManager gitManager,
            ISmoManager smoManager,
            ScriptExporter scriptExporter,
            IUserNotifier notifier,
            IFileSaveDialog saveDialog,
            IBackgroundScheduler scheduler,
            BusyState busy)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _gitManager = gitManager ?? throw new ArgumentNullException(nameof(gitManager));
            _smoManager = smoManager ?? throw new ArgumentNullException(nameof(smoManager));
            _scriptExporter = scriptExporter ?? throw new ArgumentNullException(nameof(scriptExporter));
            _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
            _saveDialog = saveDialog ?? throw new ArgumentNullException(nameof(saveDialog));
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            Busy = busy ?? throw new ArgumentNullException(nameof(busy));

            CompareCommand = new RelayCommand(Compare, () => HasTarget && !Busy.IsBusy);
            SaveScriptCommand = new RelayCommand(SaveScript, () => HasResult && !Busy.IsBusy);

            Busy.Changed += (s, e) => RaiseCanExecuteChanged();
        }

        public BusyState Busy { get; }

        public ObservableCollection<DifferenceItemViewModel> Differences { get; } =
            new ObservableCollection<DifferenceItemViewModel>();

        private DifferenceItemViewModel? _selectedDifference;
        public DifferenceItemViewModel? SelectedDifference
        {
            get => _selectedDifference;
            set
            {
                if (ReferenceEquals(_selectedDifference, value)) return;
                _selectedDifference = value;
                OnPropertyChanged();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>선택이 바뀌면 뷰가 Diff를 다시 그리도록 알린다.</summary>
        public event EventHandler? SelectionChanged;

        private string? _summaryText;

        /// <summary>"12개 중 3개 차이" 또는 "일치합니다". 검사 전에는 null이다.</summary>
        public string? SummaryText
        {
            get => _summaryText;
            private set
            {
                if (_summaryText == value) return;
                _summaryText = value;
                OnPropertyChanged();
            }
        }

        /// <summary>검사가 한 번이라도 끝났는지. 스크립트 생성의 전제다.</summary>
        public bool HasResult => _lastResult != null;

        private bool HasTarget => !string.IsNullOrWhiteSpace(_serverName) && !string.IsNullOrWhiteSpace(_databaseName);

        public ICommand CompareCommand { get; }
        public ICommand SaveScriptCommand { get; }

        /// <summary>
        /// 대상을 바꾼다. 이전 결과를 지운다 — 낡은 목록을 최신인 척 보여주지 않는다.
        /// </summary>
        public void SetTarget(string? serverName, string? databaseName, MappingMode mode)
        {
            _serverName = serverName;
            _databaseName = databaseName;
            _mode = mode;

            _lastResult = null;
            SelectedDifference = null;
            Differences.Clear();
            SummaryText = null;
            OnPropertyChanged(nameof(HasResult));
            RaiseCanExecuteChanged();
        }

        /// <summary>
        /// 선택된 객체의 좌우 원문. 왼쪽은 브랜치의 파일, 오른쪽은 DB의 현재 모습이다.
        /// 뷰가 Diff를 그릴 때만 부르므로 여기서만 SMO를 한 번 더 탄다.
        /// </summary>
        public (string BranchText, string DatabaseText) LoadSelectedTexts()
        {
            var selected = SelectedDifference;
            if (selected == null || !HasTarget) return (string.Empty, string.Empty);

            var mapping = _configManager.TryGetMapping(_serverName!, _databaseName!);
            var branchText = string.Empty;

            if (mapping != null)
            {
                var full = Path.Combine(mapping.GitPath, selected.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) branchText = File.ReadAllText(full);
            }

            // DB에만 있는 객체는 왼쪽이, 브랜치에만 있는 객체는 오른쪽이 빈다.
            var databaseText = selected.Difference.State == ObjectDiffState.MissingInDatabase
                ? string.Empty
                : _smoManager.ScriptObjectToText(_serverName!, _databaseName!, selected.QualifiedName) ?? string.Empty;

            return (branchText, databaseText);
        }

        private void Compare()
        {
            if (!HasTarget) return;

            var server = _serverName!;
            var database = _databaseName!;
            var mode = _mode;

            _comparison?.Dispose();
            _comparison = new CancellationTokenSource();
            var token = _comparison.Token;

            Busy.IsBusy = true;
            Busy.IsCancellable = true;
            Busy.ProgressText = "원격 저장소에서 가져오는 중...";

            var progress = new ExtractionProgressRelay(p =>
            {
                var text = p.Total > 0
                    ? $"비교하는 중... {p.Completed}/{p.Total} — {p.CurrentObject}"
                    : "비교하는 중...";
                _scheduler.Post(() => Busy.ProgressText = text);
            });

            _scheduler.Run(
                () =>
                {
                    // 낡은 브랜치로 비교하면 방금 병합된 변경이 목록에서 통째로 빠지고,
                    // 그것은 "배포 완료"로 보인다. 원격이 없으면 Core가 NoRemote를 돌려준다.
                    _gitManager.PullChanges(server, database);
                    return _smoManager.CompareWithRepository(server, database, progress, token);
                },
                result => ApplyComparison(result, mode),
                ex =>
                {
                    EndBusy();
                    if (ex is OperationCanceledException) return;
                    _notifier.ShowError("DBVC 차이 검사 실패", ex.Message);
                });
        }

        private void ApplyComparison(ComparisonResult? result, MappingMode mode)
        {
            EndBusy();

            if (result == null)
            {
                _notifier.ShowError("DBVC 차이 검사 실패",
                    "대상 데이터베이스에 연결하지 못했거나 매핑된 저장소가 없어 비교하지 못했습니다.");
                return;
            }

            _lastResult = result;
            SelectedDifference = null;
            Differences.Clear();

            foreach (var difference in result.Differences.OrderBy(d => d.QualifiedName, StringComparer.OrdinalIgnoreCase))
            {
                Differences.Add(new DifferenceItemViewModel(difference, mode));
            }

            SummaryText = BuildSummaryText(result);

            if (result.FailedObjects.Count > 0)
            {
                // 실패는 "차이가 없다"가 아니라 "모른다"이다. 목록에 섞으면 배포 대상으로 읽힌다.
                _notifier.ShowError("DBVC 차이 검사",
                    $"{result.FailedObjects.Count}개 객체는 스크립팅에 실패해 판정하지 못했습니다:" + Environment.NewLine +
                    string.Join(", ", result.FailedObjects));
            }

            OnPropertyChanged(nameof(HasResult));
            RaiseCanExecuteChanged();
        }

        /// <summary>
        /// ComparisonResult.IsInSync는 Differences만 본다 — Core 계층에서는 그것이 맞는 뜻이다.
        /// 그러나 화면 요약에서 그대로 쓰면 판정 실패(FailedObjects)가 있어도 "일치합니다"라고
        /// 말해 버린다. 스크립팅 실패는 "차이 없음"이 아니라 "모른다"이므로, 판정하지 못한
        /// 객체가 하나라도 있으면 요약에 그 사실을 반드시 함께 적는다 — 오류 대화상자를 닫고
        /// 요약만 본 사용자가 "배포 완료"로 읽지 않도록.
        /// </summary>
        private static string BuildSummaryText(ComparisonResult result)
        {
            var comparedText = result.Differences.Count == 0
                ? $"대상 {result.ComparedCount}개를 검사했습니다. 브랜치와 일치합니다."
                : $"대상 {result.ComparedCount}개 중 {result.Differences.Count}개가 다릅니다.";

            return result.FailedObjects.Count == 0
                ? comparedText
                : comparedText + $" {result.FailedObjects.Count}개는 판정하지 못했습니다.";
        }

        private void SaveScript()
        {
            if (_lastResult == null || !HasTarget) return;

            var export = _scriptExporter.ExportFromComparison(
                _serverName!, _databaseName!, _lastResult.Differences, DateTimeOffset.Now);

            if (!export.HasContent)
            {
                _notifier.ShowInfo("DBVC 배포 스크립트",
                    "스크립트에 담을 내용이 없어 생성할 것이 없습니다." + Environment.NewLine +
                    "차이가 전부 수동 처리 또는 확인 대상입니다.");
                return;
            }

            var path = _saveDialog.PromptForSavePath("배포 스크립트를 저장할 위치를 선택하세요.", "dbvc_deploy.sql");
            if (path == null) return;

            try
            {
                File.WriteAllText(path, export.Script);
            }
            catch (Exception ex)
            {
                _notifier.ShowError("DBVC 배포 스크립트 저장 실패", ex.Message);
                return;
            }

            var message = $"{export.IncludedCount}개 객체를 담았습니다." + Environment.NewLine + path;
            if (export.ExcludedObjects.Count > 0)
            {
                message += Environment.NewLine + Environment.NewLine +
                           $"{export.ExcludedObjects.Count}개는 제외했습니다. 사유는 파일 머리말에 있습니다.";
            }
            message += Environment.NewLine + Environment.NewLine +
                       "SSMS 쿼리 창에서 실행한 뒤 [차이 검사]를 다시 눌러 결과를 확인하세요.";

            _notifier.ShowInfo("DBVC 배포 스크립트", message);
        }

        private void EndBusy()
        {
            Busy.IsBusy = false;
            Busy.IsCancellable = false;
            Busy.ProgressText = null;
        }

        /// <summary>진행 중인 비교를 멈춘다. 저장소에 쓴 것이 없으므로 되돌릴 것이 없다.</summary>
        public void Cancel()
        {
            _comparison?.Cancel();
            Busy.ProgressText = "취소하는 중...";
        }

        private void RaiseCanExecuteChanged()
        {
            (CompareCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveScriptCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

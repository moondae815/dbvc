using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using DBVC.Core;
using DBVC.Core.Models;
using DBVC.Vsix.Services;
using DiffPlex.DiffBuilder.Model;

namespace DBVC.Vsix.ViewModels
{
    /// <summary>
    /// 선택된 객체의 커밋 이력을 보여준다. (Feature 7)
    /// ViewChangesViewModel이 이미 크므로 이력 로직은 처음부터 여기에 둔다.
    /// </summary>
    public class ObjectHistoryViewModel : INotifyPropertyChanged
    {
        private readonly IGitManager _gitManager;
        private readonly DiffService _diffService;
        private readonly IBackgroundScheduler _scheduler;

        /// <summary>
        /// 겹친 변경 파일 목록 요청 중 가장 나중 것만 화면에 반영하기 위한 표. Diff 요청용
        /// <see cref="_diffToken"/>과 따로 둔다 — 하나를 같이 쓰면, 방금 고른 커밋의 파일 목록
        /// 요청이 그 직후에 보낸 Diff 요청과 표를 다투다가 어느 한쪽 결과가 조용히 버려진다.
        /// </summary>
        private int _changedFilesToken;

        /// <summary>겹친 Diff 요청 중 가장 나중 것만 반영하기 위한 표. 용도는 <see cref="_changedFilesToken"/> 참고.</summary>
        private int _diffToken;

        public ObjectHistoryViewModel(IGitManager gitManager)
            : this(gitManager, new DiffService(), new InlineBackgroundScheduler())
        {
        }

        public ObjectHistoryViewModel(IGitManager gitManager, DiffService diffService)
            : this(gitManager, diffService, new InlineBackgroundScheduler())
        {
        }

        public ObjectHistoryViewModel(IGitManager gitManager, DiffService diffService, IBackgroundScheduler scheduler)
        {
            _gitManager = gitManager ?? throw new ArgumentNullException(nameof(gitManager));
            _diffService = diffService ?? throw new ArgumentNullException(nameof(diffService));
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        }

        public string? ServerName { get; set; }
        public string? DatabaseName { get; set; }
        public string? RelativePath { get; set; }

        public ObservableCollection<HistoryEntryViewModel> Entries { get; } = new ObservableCollection<HistoryEntryViewModel>();
        public ObservableCollection<HistoryChangedFileViewModel> ChangedFiles { get; } = new ObservableCollection<HistoryChangedFileViewModel>();

        /// <summary>단일 객체 이력 보기 모드인지 여부. <see cref="RelativePath"/>가 지정되어 있으면 단일 객체 모드다.</summary>
        public bool IsSingleObjectMode => !string.IsNullOrWhiteSpace(RelativePath);

        /// <summary>비어 있으면 화면이 목록 대신 안내 문구를 보여준다.</summary>
        public bool IsEmpty => Entries.Count == 0;

        /// <summary>선택된 객체가 없을 때 <see cref="ScopeLabel"/>이 쓰는 문구.</summary>
        private const string WholeRepositoryScope = "저장소 전체";

        /// <summary>
        /// 지금 보고 있는 이력의 범위. 목록만으로는 저장소 전체인지 특정 객체인지 구분할 수 없다.
        /// </summary>
        public string ScopeLabel { get; private set; } = string.Empty;

        private HistoryEntryViewModel? _selectedEntry;
        public HistoryEntryViewModel? SelectedEntry
        {
            get => _selectedEntry;
            set
            {
                if (ReferenceEquals(_selectedEntry, value)) return;
                _selectedEntry = value;
                OnPropertyChanged();

                ChangedFiles.Clear();
                SetChangedFilesNotice(null);
                _selectedChangedFile = null;
                OnPropertyChanged(nameof(SelectedChangedFile));

                LoadChangedFiles();
                UpdateDiffModel();
            }
        }

        /// <summary>
        /// 전체 이력 모드에서만 변경 파일 목록을 읽는다. 필터 모드는 볼 파일이 이미 정해져 있다.
        /// </summary>
        private void LoadChangedFiles()
        {
            if (_selectedEntry == null || IsSingleObjectMode || ServerName == null || DatabaseName == null)
            {
                return;
            }

            var entry = _selectedEntry;
            var token = ++_changedFilesToken;
            var server = ServerName;
            var database = DatabaseName;
            var sha = ShaOf(entry);

            _scheduler.Run(
                () => _gitManager.GetCommitDetail(server, database, sha, null),
                detail =>
                {
                    // 늦게 끝난 앞선 요청이다. 지금 화면이 보는 커밋과 다르므로 버린다.
                    if (token != _changedFilesToken) return;

                    ChangedFiles.Clear();
                    foreach (var file in detail.ChangedFiles ?? new List<HistoryChangedFile>())
                    {
                        if (file != null) ChangedFiles.Add(HistoryChangedFileViewModel.From(file));
                    }

                    SetChangedFilesNotice(BuildNotice(entry, detail));
                },
                ex =>
                {
                    Debug.WriteLine($"ObjectHistoryViewModel.LoadChangedFiles failed: {ex.Message}");

                    // 늦게 끝난 앞선 요청의 실패다. 지금 화면이 보는 커밋과 다르므로 버린다 -
                    // 성공 콜백과 같은 표를 써야 나중 요청의 안내를 이 실패가 지우지 않는다.
                    if (token != _changedFilesToken) return;

                    // Diff 패널과 달리 변경 파일 목록은 실패해도 화면이 조용히 비어 보일 뿐이라
                    // "이 커밋은 변경이 없다"와 "읽기 실패"를 구분할 수 없다. ChangedFilesNotice는
                    // 이미 바인딩되어 있으므로(모달 없이) 여기서만 실패를 알린다 - Diff 쪽은
                    // 화면마다 커밋 선택 때 매번 뜨는 모달을 새로 만들어야 해서 범위 밖이다.
                    SetChangedFilesNotice("변경된 파일 목록을 읽지 못했습니다.");
                });
        }

        private static string? BuildNotice(HistoryEntryViewModel entry, CommitDetail detail)
        {
            var parts = new List<string>();
            if (entry.ParentCount > 1) parts.Add("병합 커밋입니다 — 첫 부모 기준으로 비교합니다.");
            // 상한 상수가 아니라 실제로 담긴 개수를 쓴다. 둘이 어긋나면 안내가 거짓말이 된다.
            if (detail.IsTruncated) parts.Add($"전체 {detail.TotalChangedFileCount}개 중 {detail.ChangedFiles?.Count ?? 0}개만 표시합니다.");
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }

        private void SetChangedFilesNotice(string? notice)
        {
            ChangedFilesNotice = notice;
            OnPropertyChanged(nameof(ChangedFilesNotice));
            OnPropertyChanged(nameof(HasChangedFilesNotice));
        }

        /// <summary>파일 목록 위에 띄울 안내. 없으면 <c>null</c>.</summary>
        public string? ChangedFilesNotice { get; private set; }

        public bool HasChangedFilesNotice => !string.IsNullOrEmpty(ChangedFilesNotice);

        /// <summary>축약 SHA는 충돌할 수 있으므로 전체 SHA가 있으면 그것을 쓴다.</summary>
        private static string ShaOf(HistoryEntryViewModel entry)
            => !string.IsNullOrEmpty(entry.Sha) ? entry.Sha : entry.ShortSha;

        private HistoryChangedFileViewModel? _selectedChangedFile;
        public HistoryChangedFileViewModel? SelectedChangedFile
        {
            get => _selectedChangedFile;
            set
            {
                if (ReferenceEquals(_selectedChangedFile, value)) return;
                _selectedChangedFile = value;
                OnPropertyChanged();
                UpdateDiffModel();
            }
        }

        private SideBySideDiffModel? _selectedDiffModel;
        public SideBySideDiffModel? SelectedDiffModel
        {
            get => _selectedDiffModel;
            private set
            {
                _selectedDiffModel = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDiffVisible));
            }
        }

        public bool IsDiffVisible => SelectedDiffModel != null;

        private string? _selectedOldText;
        private string? _selectedNewText;

        /// <summary>
        /// 외부 비교 창에 넘길 원본. Diff 모델에서 되짚어 만들면 줄 끝과 마지막 개행이 달라져
        /// 내장 뷰와 외부 창이 서로 다른 결과를 보인다.
        /// </summary>
        public (string OldText, string NewText)? GetSelectedFileTexts()
            => _selectedOldText == null || _selectedNewText == null ? null : (_selectedOldText, _selectedNewText);

        private void UpdateDiffModel()
        {
            var targetPath = IsSingleObjectMode ? RelativePath : _selectedChangedFile?.RelativePath;
            if (_selectedEntry == null || ServerName == null || DatabaseName == null || string.IsNullOrWhiteSpace(targetPath))
            {
                _selectedOldText = null;
                _selectedNewText = null;
                SelectedDiffModel = null;
                return;
            }

            var token = ++_diffToken;
            var server = ServerName;
            var database = DatabaseName;
            var sha = ShaOf(_selectedEntry);
            var path = targetPath!;

            _scheduler.Run(
                () => _gitManager.GetCommitDetail(server, database, sha, path),
                detail =>
                {
                    if (token != _diffToken) return;
                    _selectedOldText = detail.OldText ?? string.Empty;
                    _selectedNewText = detail.NewText ?? string.Empty;
                    SelectedDiffModel = _diffService.GetDiffModelFromString(detail.OldText ?? string.Empty, detail.NewText ?? string.Empty);
                },
                ex => Debug.WriteLine($"ObjectHistoryViewModel.UpdateDiffModel failed: {ex.Message}"));
        }

        /// <summary>
        /// 이력을 다시 읽는다. 서버나 데이터베이스가 비면 목록을 비운 상태로 끝낸다.
        ///
        /// <paramref name="relativePath"/>는 비어도 된다 — 그 경우 저장소 전체 이력을 읽는다.
        /// 전에는 경로가 없으면 목록을 비웠는데, 그러면 커밋 직후처럼 변경 목록이 비어
        /// 선택할 객체가 아예 없는 상황에서 방금 만든 커밋조차 볼 수 없었다.
        /// </summary>
        public void Load(string? serverName, string? databaseName, string? relativePath)
        {
            ServerName = serverName;
            DatabaseName = databaseName;
            RelativePath = relativePath;

            Entries.Clear();
            ChangedFiles.Clear();
            SetChangedFilesNotice(null);
            SelectedChangedFile = null;
            ScopeLabel = string.Empty;
            SelectedEntry = null;

            if (!string.IsNullOrWhiteSpace(serverName) && !string.IsNullOrWhiteSpace(databaseName))
            {
                var history = _gitManager.GetHistory(serverName!, databaseName!, relativePath)
                    ?? (IReadOnlyList<CommitInfo>)new List<CommitInfo>();

                foreach (var commit in history)
                {
                    if (commit == null) continue;
                    Entries.Add(HistoryEntryViewModel.From(commit));
                }

                ScopeLabel = DescribeScope(relativePath);
            }

            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ScopeLabel));
            OnPropertyChanged(nameof(IsSingleObjectMode));
        }

        /// <summary>
        /// 범위를 설명한다. 객체는 경로가 아니라 사용자가 개체 탐색기에서 보는 이름으로 부른다.
        /// </summary>
        private static string DescribeScope(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return WholeRepositoryScope;

            return ObjectPathConvention.TryParseRelativePath(relativePath, out var schema, out _, out var objectName)
                ? ObjectPathConvention.GetQualifiedName(schema, objectName)
                : relativePath!;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// 이력 목록의 한 행.
    /// SHA 축약과 날짜 서식은 화면 관심사이므로 Core의 <see cref="CommitInfo"/>에 두지 않는다.
    /// </summary>
    public class HistoryEntryViewModel
    {
        private const int ShortShaLength = 7;

        public string Sha { get; set; } = string.Empty;
        public string? ParentSha { get; set; }
        public string ShortSha { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public int ParentCount { get; set; }

        /// <summary>목록에 그대로 찍는 병합 표시. 컨버터를 두지 않으려고 문자열로 낸다.</summary>
        public string MergeMark => ParentCount > 1 ? "병합" : string.Empty;

        public bool HasParent => !string.IsNullOrEmpty(ParentSha);

        public static HistoryEntryViewModel From(CommitInfo commit)
        {
            return new HistoryEntryViewModel
            {
                Sha = commit.Sha ?? string.Empty,
                ParentSha = commit.ParentSha,
                ParentCount = commit.ParentCount,
                ShortSha = Shorten(commit.Sha),
                Message = FirstLine(commit.Message),
                Author = commit.Author ?? string.Empty,
                Date = commit.Date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            };
        }

        private static string Shorten(string? sha)
        {
            if (string.IsNullOrEmpty(sha)) return string.Empty;
            return sha!.Length > ShortShaLength ? sha.Substring(0, ShortShaLength) : sha!;
        }

        /// <summary>커밋 메시지는 여러 줄일 수 있다. 목록에는 첫 줄만 보여준다.</summary>
        private static string FirstLine(string? message)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;

            var index = message!.IndexOfAny(new[] { '\r', '\n' });
            return (index < 0 ? message! : message!.Substring(0, index)).Trim();
        }
    }
}

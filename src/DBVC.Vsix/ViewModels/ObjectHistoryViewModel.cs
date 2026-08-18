using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Vsix.ViewModels
{
    /// <summary>
    /// 선택된 객체의 커밋 이력을 보여준다. (Feature 7)
    /// ViewChangesViewModel이 이미 크므로 이력 로직은 처음부터 여기에 둔다.
    /// </summary>
    public class ObjectHistoryViewModel : INotifyPropertyChanged
    {
        private readonly IGitManager _gitManager;

        public ObjectHistoryViewModel(IGitManager gitManager)
        {
            _gitManager = gitManager ?? throw new ArgumentNullException(nameof(gitManager));
        }

        public ObservableCollection<HistoryEntryViewModel> Entries { get; } = new ObservableCollection<HistoryEntryViewModel>();

        /// <summary>비어 있으면 화면이 목록 대신 안내 문구를 보여준다.</summary>
        public bool IsEmpty => Entries.Count == 0;

        /// <summary>선택된 객체가 없을 때 <see cref="ScopeLabel"/>이 쓰는 문구.</summary>
        private const string WholeRepositoryScope = "저장소 전체";

        /// <summary>
        /// 지금 보고 있는 이력의 범위. 목록만으로는 저장소 전체인지 특정 객체인지 구분할 수 없다.
        /// </summary>
        public string ScopeLabel { get; private set; } = string.Empty;

        /// <summary>
        /// 이력을 다시 읽는다. 서버나 데이터베이스가 비면 목록을 비운 상태로 끝낸다.
        ///
        /// <paramref name="relativePath"/>는 비어도 된다 — 그 경우 저장소 전체 이력을 읽는다.
        /// 전에는 경로가 없으면 목록을 비웠는데, 그러면 커밋 직후처럼 변경 목록이 비어
        /// 선택할 객체가 아예 없는 상황에서 방금 만든 커밋조차 볼 수 없었다.
        /// </summary>
        public void Load(string? serverName, string? databaseName, string? relativePath)
        {
            Entries.Clear();
            ScopeLabel = string.Empty;

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

        public string ShortSha { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;

        public static HistoryEntryViewModel From(CommitInfo commit)
        {
            return new HistoryEntryViewModel
            {
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

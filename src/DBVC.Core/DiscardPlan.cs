using System;
using System.Collections.Generic;
using System.Linq;

namespace DBVC.Core
{
    /// <summary>
    /// 되돌리기의 대상을 가른다. Git도 DB도 닿지 않는 순수 함수라 판정의 시험대가 여기 하나다.
    ///
    /// 화면은 이것으로 확인 문구를 만들고 GitManager는 이것으로 실행 대상을 정한다.
    /// 판정이 두 곳에 생기면 갈라지고, 갈라지는 날 "지울 파일 0개"라 말해 놓고 지운다.
    /// (MappingPolicy를 CanExecute와 Core 진입부가 함께 부르는 것과 같은 구조다.)
    /// </summary>
    public class DiscardPlan
    {
        private const string AddedState = "Added";

        /// <summary>추적 중인 파일. HEAD 내용으로 되돌린다.</summary>
        public List<string> RestorePaths { get; } = new List<string>();

        /// <summary>미추적 파일. 지우는 것 외에 되돌릴 방법이 없다.</summary>
        public List<string> DeletePaths { get; } = new List<string>();

        /// <summary>대상이 아닌 경로. 사용자가 체크한 것이 조용히 빠지면 안 되므로 남긴다.</summary>
        public List<string> SkippedPaths { get; } = new List<string>();

        public bool IsEmpty => RestorePaths.Count == 0 && DeletePaths.Count == 0;

        public static DiscardPlan Build(
            IEnumerable<string>? requestedPaths,
            IReadOnlyDictionary<string, string>? gitStates)
        {
            var plan = new DiscardPlan();

            foreach (var path in requestedPaths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                string? state = null;
                gitStates?.TryGetValue(path, out state);

                // Git이 지금 변경으로 보고하지 않는 경로는 되돌릴 것이 없다. 화면 목록은
                // 사용자가 들여다본 만큼 낡으므로, 그 사이에 깨끗해진 파일을 HEAD로 덮어쓰면
                // 되돌리기가 아니라 손실이 된다.
                if (state == null)
                {
                    plan.SkippedPaths.Add(path);
                    continue;
                }

                // DBVC의 경로 규약을 따르지 않는 파일은 DBVC가 만든 것이 아니다.
                // README·.gitattributes·추출 기준선 표식이 이 검사로 보호된다.
                if (!ObjectPathConvention.TryParseRelativePath(path, out _, out _, out _)
                    || EscapesRoot(path))
                {
                    plan.SkippedPaths.Add(path);
                    continue;
                }

                if (string.Equals(state, AddedState, StringComparison.OrdinalIgnoreCase))
                {
                    plan.DeletePaths.Add(path);
                }
                else
                {
                    plan.RestorePaths.Add(path);
                }
            }

            return plan;
        }

        /// <summary>
        /// ".." 세 조각은 경로 규약 검사를 그대로 통과한다(WorkingTreeCleaner에 같은 함정이
        /// 기록되어 있다). 실제 Git 상태에는 이런 경로가 오지 않지만, 판정이 순수 함수인 이상
        /// 입력을 지어낼 수 있는 호출자를 상대로도 성립해야 한다.
        /// </summary>
        private static bool EscapesRoot(string path)
        {
            return path.Replace('\\', '/')
                .Split('/')
                .Any(segment => segment == ".." || segment == ".");
        }
    }

    /// <summary>
    /// 되돌리기의 결과. CleanupResult와 같은 모양이다 — 실패한 경로는 사용자에게 알려야 한다.
    /// </summary>
    public class DiscardResult
    {
        public List<string> RestoredPaths { get; } = new List<string>();
        public List<string> DeletedPaths { get; } = new List<string>();
        public List<string> FailedPaths { get; } = new List<string>();
        public List<string> SkippedPaths { get; } = new List<string>();

        public bool HasFailures => FailedPaths.Count > 0;
    }
}

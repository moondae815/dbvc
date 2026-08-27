using System;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// 저장소를 그대로 써도 되는지 판정한다. LibGit2Sharp에 닿지 않는 순수 함수라
    /// 저장소 없이 테스트된다 — 판정 로직은 여기, 값 읽기는 GitManager가 맡는다.
    /// </summary>
    public static class RepositoryStateEvaluator
    {
        public static RepositoryBlockReason Evaluate(
            string? currentBranch,
            bool isDetached,
            string? pendingOperation,
            string? expectedBranch,
            MappingMode mode = MappingMode.Write,
            bool hasUncommittedChanges = false)
        {
            // 병합 중이면 브랜치 이름이 맞아도 작업 트리가 중간 상태다. 브랜치 불일치보다
            // 먼저 알려야 사용자가 "브랜치를 바꾸면 되겠구나"로 오해하지 않는다.
            if (!string.IsNullOrWhiteSpace(pendingOperation))
            {
                return RepositoryBlockReason.OperationInProgress;
            }

            // 고정이 없어도 막는다. detached에서 커밋하면 어느 브랜치에도 남지 않는다.
            if (isDetached)
            {
                return RepositoryBlockReason.DetachedHead;
            }

            // 고정이 없으면 어느 브랜치든 정상이다(개발 클론).
            if (string.IsNullOrWhiteSpace(expectedBranch))
            {
                return DeniesDirtyWorkingTree(mode, hasUncommittedChanges)
                    ? RepositoryBlockReason.WorkingTreeDirty
                    : RepositoryBlockReason.None;
            }

            if (!string.Equals(currentBranch, expectedBranch, StringComparison.OrdinalIgnoreCase))
            {
                return RepositoryBlockReason.BranchMismatch;
            }

            // 마지막에 본다. enum의 순서가 곧 우선순위이고, 이것이 가장 약한 사유다 —
            // 브랜치가 틀린 채로 "커밋되지 않은 변경이 있습니다"를 띄우면 사용자가
            // 그것을 정리한 뒤에야 진짜 이유를 만난다.
            return DeniesDirtyWorkingTree(mode, hasUncommittedChanges)
                ? RepositoryBlockReason.WorkingTreeDirty
                : RepositoryBlockReason.None;
        }

        /// <summary>
        /// 개발 클론(write)에서 더러운 트리는 추출 직후의 정상 상태다. 배포·감사 클론은
        /// 커밋하지 않으므로 정상이면 항상 깨끗하고, 더럽다면 비교 기준을 믿을 수 없다.
        /// </summary>
        private static bool DeniesDirtyWorkingTree(MappingMode mode, bool hasUncommittedChanges)
        {
            return mode != MappingMode.Write && hasUncommittedChanges;
        }

        public static string? BuildMessage(
            RepositoryBlockReason reason, string? currentBranch, string? expectedBranch, string? pendingOperation)
        {
            switch (reason)
            {
                case RepositoryBlockReason.OperationInProgress:
                    return $"저장소에 끝나지 않은 작업({pendingOperation})이 남아 있어 DBVC를 사용할 수 없습니다. " +
                           "Git 클라이언트에서 그 작업을 끝내거나 되돌린 뒤 다시 시도하세요.";

                case RepositoryBlockReason.DetachedHead:
                    return "저장소가 어느 브랜치도 가리키지 않는 상태(detached HEAD)여서 DBVC를 사용할 수 없습니다. " +
                           "Git 클라이언트에서 브랜치를 체크아웃한 뒤 다시 시도하세요.";

                case RepositoryBlockReason.BranchMismatch:
                    return $"이 대상은 '{expectedBranch}' 브랜치에 고정되어 있는데 저장소는 '{currentBranch}'에 있습니다. " +
                           "그대로 두면 비교 결과가 사실과 달라지므로 중단했습니다. " +
                           $"Git 클라이언트에서 '{expectedBranch}'를 체크아웃한 뒤 다시 시도하세요.";

                case RepositoryBlockReason.WorkingTreeDirty:
                    return "이 저장소에 커밋되지 않은 변경이 있어 비교 기준을 믿을 수 없습니다. " +
                           "배포·감사용 저장소는 브랜치의 내용 그대로여야 합니다. " +
                           "Git 클라이언트에서 변경을 되돌린 뒤 다시 시도하세요.";

                default:
                    return null;
            }
        }
    }
}

using System;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>DBVC가 대상에 대해 할 수 있는 동작. mode 판정의 축이다.</summary>
    public enum DbvcOperation
    {
        /// <summary>DDL 트리거와 ChangeLog 설치·갱신.</summary>
        InstallTracker,

        /// <summary>저장소에 파일을 쓰는 추출.</summary>
        Extract,

        Commit,
        Push,

        /// <summary>대상 DB와 브랜치의 차이 검사. 저장소에 쓰지 않는다.</summary>
        Compare,

        GenerateScript
    }

    /// <summary>
    /// mode별 허용 동작을 정하는 유일한 자리. 순수 함수라 DB·Git 없이 테스트된다.
    ///
    /// 화면과 Core가 각자 판정하면 언젠가 갈라지고, 갈라진 쪽이 이기는 날 배포 클론에서
    /// 커밋이 나간다. 그래서 ViewModel의 CanExecute와 Core API 진입부가 모두 이 함수를 부른다.
    ///
    /// 이것은 실수를 막는 장치이지 보안 장치가 아니다 — mappings.json은 사용자가 편집할 수
    /// 있는 로컬 파일이다. 실제 권한은 SQL Server 계정 권한이 담당한다.
    /// </summary>
    public static class MappingPolicy
    {
        public static bool IsAllowed(MappingMode mode, DbvcOperation operation)
        {
            switch (operation)
            {
                case DbvcOperation.InstallTracker:
                case DbvcOperation.Extract:
                case DbvcOperation.Commit:
                case DbvcOperation.Push:
                    // 테스트 DB에서 나온 추출물은 새 변경이 아니라 배포 결과다. 커밋하면
                    // develop에 자기 자신을 되먹이고, 배포가 덜 된 상태였다면 그 상태를
                    // 정답으로 굳혀 버린다.
                    return mode == MappingMode.Write;

                case DbvcOperation.Compare:
                    // 개발 DB는 master + 진행 중인 모든 feature 상태라 차이 전체가 잡음이다.
                    return mode != MappingMode.Write;

                case DbvcOperation.GenerateScript:
                    return true;

                default:
                    // 새 동작이 생겼는데 이 표를 고치지 않으면 조용히 허용되는 대신 죽어야 한다.
                    throw new InvalidOperationException($"처리되지 않은 {nameof(DbvcOperation)}: {operation}");
            }
        }

        public static string BuildDeniedMessage(MappingMode mode, DbvcOperation operation)
        {
            return $"이 대상은 '{GetModeName(mode)}' 용도로 등록되어 있어 {GetOperationName(operation)}을(를) 할 수 없습니다. " +
                   "용도를 바꾸려면 저장소를 다시 연결하세요.";
        }

        private static string GetModeName(MappingMode mode)
        {
            switch (mode)
            {
                case MappingMode.Write: return "개발";
                case MappingMode.Deploy: return "배포";
                case MappingMode.Audit: return "감사";
                default: throw new InvalidOperationException($"처리되지 않은 {nameof(MappingMode)}: {mode}");
            }
        }

        private static string GetOperationName(DbvcOperation operation)
        {
            switch (operation)
            {
                case DbvcOperation.InstallTracker: return "변경 추적 설치";
                case DbvcOperation.Extract: return "저장소 추출";
                case DbvcOperation.Commit: return "커밋";
                case DbvcOperation.Push: return "Push";
                case DbvcOperation.Compare: return "차이 검사";
                case DbvcOperation.GenerateScript: return "배포 스크립트 생성";
                default: throw new InvalidOperationException($"처리되지 않은 {nameof(DbvcOperation)}: {operation}");
            }
        }
    }
}

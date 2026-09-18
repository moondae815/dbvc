using System;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// 환경 브랜치 이름. 병합 원본이 될 수 없다.
    ///
    /// 티켓 이름 규칙은 조직이 바꿀 수 있어 코드에 넣지 않았지만(브랜치 조작 설계 3.5), 이 둘은
    /// 워크플로 설계 1.1이 전제로 삼은 환경 브랜치라 성격이 다르다. 대소문자는 git과 같이 구분한다.
    ///
    /// 이름을 아는 판단은 전부 이 파일에 모은다. 흩어 놓으면 조직이 이름을 바꾸기로 하는 날
    /// 고칠 자리를 찾는 것부터 일이 된다(팀 배포 백로그 13번).
    /// </summary>
    public static class EnvironmentBranches
    {
        public const string Develop = "develop";
        public const string Master = "master";

        public static bool IsEnvironmentBranch(string? name)
        {
            return string.Equals(name, Develop, StringComparison.Ordinal)
                || string.Equals(name, Master, StringComparison.Ordinal);
        }

        /// <summary>
        /// 이 용도의 클론이 고정할 만한 브랜치. 개발 용도는 null이다.
        ///
        /// 개발 클론에 이름을 채워 주면 칸을 비우지 않은 사람이 2026-09-09에 철회한 "develop 고정"을
        /// 그대로 뒤집어쓴다 - 티켓 브랜치에서 커밋하고 develop에 병합하는 흐름 자체가 막힌다
        /// (브랜치 정책 정정 설계 5절).
        ///
        /// 제안일 뿐이라 이름이 다른 조직은 고쳐 적으면 된다. 대화상자가 검증에 쓰지 않는다.
        /// </summary>
        public static string? SuggestFor(MappingMode mode)
        {
            switch (mode)
            {
                case MappingMode.Write: return null;
                case MappingMode.Deploy: return Develop;
                case MappingMode.Audit: return Master;
                default: throw new InvalidOperationException($"처리되지 않은 {nameof(MappingMode)}: {mode}");
            }
        }

        /// <summary>
        /// 이 브랜치에 커밋하기 전에 사람에게 물어야 하는지.
        ///
        /// 개발 DB는 정의상 master + 진행 중인 모든 feature다. 그 추출물을 master에 커밋하면
        /// 아직 나가면 안 되는 변경이 운영 기준선이 되고, 감사 클론은 그것을 "운영에 있어야 할 것"
        /// 으로 읽어 차이를 보지 못한다 - 이 도구를 도입한 이유가 그 자리에서 사라진다.
        ///
        /// develop은 묻지 않는다. 개발자에게 직접 Push 권한이 있고 병합으로 자정되는 경로이며,
        /// 무엇보다 자주 뜨는 경고는 값을 잃어 진짜 경고(CoAuthorDetector)까지 함께 묻힌다.
        ///
        /// 배포·감사는 커밋 자체가 금지라(MappingPolicy) 물을 일이 없다.
        /// </summary>
        public static bool WarnsBeforeCommit(MappingMode mode, string? currentBranch)
        {
            return mode == MappingMode.Write
                && string.Equals(currentBranch, Master, StringComparison.Ordinal);
        }
    }
}

using System;

namespace DBVC.Core
{
    /// <summary>
    /// 환경 브랜치 이름. 병합 원본이 될 수 없다.
    ///
    /// 티켓 이름 규칙은 조직이 바꿀 수 있어 코드에 넣지 않았지만(브랜치 조작 설계 3.5), 이 둘은
    /// 워크플로 설계 1.1이 전제로 삼은 환경 브랜치라 성격이 다르다. 대소문자는 git과 같이 구분한다.
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
    }
}

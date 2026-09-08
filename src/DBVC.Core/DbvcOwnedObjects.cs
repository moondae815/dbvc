using System;

namespace DBVC.Core
{
    /// <summary>
    /// DBVC가 대상 DB에 만드는 객체를 가른다. 추적하지도(DDL 트리거) 추출하지도(SMO) 않는다.
    ///
    /// 이름을 하나씩 보태지 않는 이유는, 객체가 늘 때마다 SQL 트리거와 SmoManager 두 곳에
    /// 같은 이름을 더해야 하고 한쪽을 빠뜨리면 도구가 자기 자신을 저장소에 커밋하기 때문이다.
    /// 접두사를 도구의 이름공간으로 선언해 그 실수를 구조적으로 없앤다.
    ///
    /// 대가: 사용자가 DBVC_로 시작하는 객체를 만들면 조용히 추적에서 빠진다. README에 적는다.
    /// </summary>
    public static class DbvcOwnedObjects
    {
        /// <summary>설치 스크립트의 LIKE N'DBVC[_]%'와 같은 값이어야 한다. InstallScriptSyncTests가 대조한다.</summary>
        public const string Prefix = "DBVC_";

        /// <summary>트리거만 접두사 규칙 밖에 있다. 기존 설치와 이름이 같아야 하므로 바꾸지 않는다.</summary>
        public const string TriggerName = "trg_DBVC_DDL_Tracker";

        public static bool IsOwned(string? objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName)) return false;

            // StartsWith가 밑줄까지 함께 요구하므로 SQL의 [_] 이스케이프와 결과가 같다.
            return objectName!.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(objectName, TriggerName, StringComparison.OrdinalIgnoreCase);
        }
    }
}

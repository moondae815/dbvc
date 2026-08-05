using System;
using System.Text;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// SMO URN에서 필요한 조각만 꺼낸다.
    ///
    /// 개체 탐색기 노드는 <c>INodeContext.Context</c>로 URN을 준다. 예:
    /// <c>Server[@Name='HOST\INST']/Database[@Name='SalesDB']/Table[@Name='Person'and@Schema='dbo']</c>
    ///
    /// 리플렉션 어댑터에서 이 로직만 떼어낸 이유는 단위 테스트다 —
    /// SSMS 프로세스 밖에서 검증할 수 있는 유일한 부분이므로 여기에 모아 둔다.
    /// </summary>
    public static class SsmsUrn
    {
        private const string DatabaseMarker = "/Database[@Name='";

        /// <summary>
        /// URN이 데이터베이스를 지목하고 있으면 그 이름을, 아니면 <c>null</c>.
        /// 서버 노드처럼 <c>Database</c> 마디가 없는 경우도 <c>null</c>이다 —
        /// 사용자가 데이터베이스를 고르지 않았다는 뜻이므로 넘겨짚지 않는다.
        /// </summary>
        public static string? TryGetDatabaseName(string? urn)
        {
            if (string.IsNullOrEmpty(urn))
            {
                return null;
            }

            int start = urn!.IndexOf(DatabaseMarker, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }
            start += DatabaseMarker.Length;

            var name = new StringBuilder();
            for (int i = start; i < urn.Length; i++)
            {
                if (urn[i] != '\'')
                {
                    name.Append(urn[i]);
                    continue;
                }

                // SMO는 값 안의 '를 ''로 이스케이프한다. 뒤따르는 문자가 '이면 닫는 따옴표가 아니다.
                if (i + 1 < urn.Length && urn[i + 1] == '\'')
                {
                    name.Append('\'');
                    i++;
                    continue;
                }

                return name.Length > 0 ? name.ToString() : null;
            }

            // 닫는 따옴표가 없다 — URN이 잘렸거나 형식이 다르다. 추측하지 않는다.
            return null;
        }
    }
}

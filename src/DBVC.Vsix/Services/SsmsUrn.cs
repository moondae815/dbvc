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

        /// <summary>
        /// SMO URN에서 데이터베이스 이름, 스키마, 객체 타입, 객체 이름을 파싱한다.
        /// URN이 데이터베이스 하위의 구체적인 객체(테이블, 뷰, 프로시저 등)를 가리킬 때만 <c>true</c>를 반환한다.
        /// </summary>
        public static bool TryParseObjectIdentity(
            string? urn,
            out string? databaseName,
            out string? schema,
            out string? objectType,
            out string? objectName)
        {
            databaseName = null;
            schema = null;
            objectType = null;
            objectName = null;

            if (string.IsNullOrEmpty(urn))
            {
                return false;
            }

            var db = TryGetDatabaseName(urn);
            if (db == null)
            {
                return false;
            }

            var lastSlash = urn!.LastIndexOf('/');
            if (lastSlash < 0)
            {
                return false;
            }

            var lastSegment = urn.Substring(lastSlash + 1);
            var bracketIndex = lastSegment.IndexOf('[');
            if (bracketIndex <= 0)
            {
                return false;
            }

            var type = lastSegment.Substring(0, bracketIndex);
            if (string.Equals(type, "Database", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var name = TryGetAttributeValue(lastSegment, "Name");
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            databaseName = db;
            objectType = type;
            objectName = name;
            schema = TryGetAttributeValue(lastSegment, "Schema");

            return true;
        }

        private static string? TryGetAttributeValue(string segment, string attributeName)
        {
            var marker = "@" + attributeName + "='";
            var start = segment.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }
            start += marker.Length;

            var value = new StringBuilder();
            for (int i = start; i < segment.Length; i++)
            {
                if (segment[i] != '\'')
                {
                    value.Append(segment[i]);
                    continue;
                }

                // SMO는 값 안의 '를 ''로 이스케이프한다. 뒤따르는 문자가 '이면 닫는 따옴표가 아니다.
                if (i + 1 < segment.Length && segment[i + 1] == '\'')
                {
                    value.Append('\'');
                    i++;
                    continue;
                }

                return value.Length > 0 ? value.ToString() : null;
            }

            return null;
        }
    }
}

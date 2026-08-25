using System;
using System.Collections.Generic;
using System.Linq;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// 커밋하려는 객체를 다른 작업자도 만졌는지 알린다.
    ///
    /// 공용 개발 DB가 하나뿐인 이상, A가 프로시저 P를 고치고 뒤이어 B도 P를 고치면 DB의 P는
    /// B의 코드다. A가 추출해 커밋하면 B의 미완성 작업이 A의 브랜치에 담긴다. 막을 방법은
    /// 구조적으로 없고 알릴 수만 있다.
    ///
    /// 차단이 아니라 경고인 이유는 대부분이 실제로 이어서 작업한 정상적인 경우이기 때문이다.
    /// 차단하면 사람들이 도구를 쓰지 않게 된다(설계 3.10).
    ///
    /// DB에도 Git에도 닿지 않는 순수 함수다.
    /// </summary>
    public static class CoAuthorDetector
    {
        public static IReadOnlyList<CoAuthorWarning> Detect(
            IEnumerable<ChangeLogRow> allPendingRows,
            IEnumerable<string> committingQualifiedNames,
            string? currentLogin,
            string? currentHost)
        {
            var targets = new HashSet<string>(
                committingQualifiedNames ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            if (targets.Count == 0) return Array.Empty<CoAuthorWarning>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<CoAuthorWarning>();

            foreach (var row in allPendingRows ?? Enumerable.Empty<ChangeLogRow>())
            {
                if (row == null) continue;

                var qualified = ObjectPathConvention.GetQualifiedName(row.SchemaName, row.ObjectName);
                if (!targets.Contains(qualified)) continue;

                if (IsCurrentAuthor(row, currentLogin, currentHost)) continue;

                // v3 이전 행은 HostName이 null이다. "내 것"으로 볼 근거가 없으므로 남의 것으로 다루고,
                // 표시는 로그인 이름으로 대신한다.
                var author = string.IsNullOrWhiteSpace(row.HostName)
                    ? (row.LoginName ?? "알 수 없음")
                    : row.HostName!;

                // 같은 사람이 같은 객체를 여러 번 만졌어도 한 번만 알린다.
                if (!seen.Add($"{qualified}|{author}")) continue;

                warnings.Add(new CoAuthorWarning { QualifiedName = qualified, Author = author });
            }

            return warnings;
        }

        private static bool IsCurrentAuthor(ChangeLogRow row, string? currentLogin, string? currentHost)
        {
            // HostName이 비어 있으면 현재 사용자와 같다고 볼 수 없다 - 비교 자체가 성립하지 않는다.
            if (string.IsNullOrWhiteSpace(row.HostName)) return false;

            return string.Equals(row.LoginName, currentLogin, StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.HostName, currentHost, StringComparison.OrdinalIgnoreCase);
        }
    }
}

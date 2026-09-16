using System;
using System.Linq;

namespace DBVC.Core
{
    /// <summary>
    /// AI에게 보낼 프롬프트를 짓고, 돌아온 응답을 커밋 메시지 한 줄로 정리한다.
    ///
    /// 정적·순수로 둔 이유: 이 기능에서 가장 자주 틀리는 자리가 여기인데,
    /// 네트워크도 Git도 끼지 않으면 그 판단만 따로 검증할 수 있다.
    /// </summary>
    public static class CommitMessageComposer
    {
        /// <summary>스키마 저장소에 해당하는 변경만 남긴 집합. docs·test는 여기 없다.</summary>
        public static readonly string[] AllowedPrefixes = { "feat", "fix", "refactor", "perf", "chore" };

        private const int MaxLength = 72;

        /// <summary>
        /// 모델 응답을 커밋 메시지 한 줄로 만든다. 빈 문자열이면 쓸 만한 응답이 없었다는 뜻이다 —
        /// 호출자가 실패로 다룬다.
        /// </summary>
        public static string Clean(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var text = raw!.Replace("\r\n", "\n").Trim();

            // 코드펜스를 씌워 돌려주는 모델이 있다. 여는 줄에 ```sql 같은 언어 표시가 붙기도 한다.
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var fenceLines = text.Split('\n').ToList();
                fenceLines.RemoveAt(0);
                if (fenceLines.Count > 0 && fenceLines[fenceLines.Count - 1].TrimEnd().StartsWith("```", StringComparison.Ordinal))
                {
                    fenceLines.RemoveAt(fenceLines.Count - 1);
                }
                text = string.Join("\n", fenceLines).Trim();
            }

            var lines = text.Split('\n')
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            if (lines.Count == 0) return string.Empty;

            // 모델이 안내문을 앞에 덧붙이는 경우가 있다 — 그럴 때 그냥 첫 줄을 집으면 안내문이
            // chore로 감싸여 진짜 메시지를 밀어낸다. 접두어가 이미 있는 줄을 먼저 찾는다.
            var firstLine = lines.FirstOrDefault(HasAllowedPrefix) ?? lines[0];

            firstLine = UnwrapQuotes(firstLine);

            if (!HasAllowedPrefix(firstLine))
            {
                firstLine = "chore: " + firstLine;
            }

            // 형식이 마침표 없음을 요구한다. 하나만 지우면 말줄임표가 비대칭으로 남아
            // (".", "..") 오히려 더 헷갈리므로 끝에 이어진 마침표는 통째로 없앤다.
            firstLine = firstLine.TrimEnd('.', '。');

            if (firstLine.Length > MaxLength)
            {
                firstLine = firstLine.Substring(0, MaxLength).TrimEnd();
            }

            return firstLine;
        }

        /// <summary>
        /// 감싸는 인용부호를 한 겹만 벗기면 그 안에 다른 종류로 한 번 더 감싼 경우
        /// (예: <c>'"..."'</c>) 안쪽 인용부호가 남는다. 바뀌지 않을 때까지 반복한다 —
        /// 반복 상한은 사람이 실수로라도 무한 루프에 들어가지 않게 하는 안전판이다.
        /// </summary>
        private static string UnwrapQuotes(string value)
        {
            for (var i = 0; i < 10; i++)
            {
                var before = value;
                value = TrimWrapper(value, '"');
                value = TrimWrapper(value, '\'');
                value = TrimWrapper(value, '`');
                if (value == before) break;
            }
            return value;
        }

        private static string TrimWrapper(string value, char wrapper)
        {
            if (value.Length >= 2 && value[0] == wrapper && value[value.Length - 1] == wrapper)
            {
                return value.Substring(1, value.Length - 2).Trim();
            }
            return value;
        }

        /// <summary>
        /// <c>feat:</c> 또는 <c>feat(scope):</c> 형태인지 본다. 스코프는 쓰지 말라고 프롬프트에
        /// 적지만 붙는 경우가 있고, 형식으로는 유효하므로 고치지 않는다.
        /// </summary>
        private static bool HasAllowedPrefix(string message)
        {
            var colon = message.IndexOf(':');
            if (colon <= 0) return false;

            var head = message.Substring(0, colon);
            var paren = head.IndexOf('(');
            if (paren > 0) head = head.Substring(0, paren);

            return AllowedPrefixes.Contains(head.Trim(), StringComparer.OrdinalIgnoreCase);
        }
    }
}

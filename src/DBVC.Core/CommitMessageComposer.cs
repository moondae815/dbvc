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
                var lines = text.Split('\n').ToList();
                lines.RemoveAt(0);
                if (lines.Count > 0 && lines[lines.Count - 1].TrimEnd().StartsWith("```", StringComparison.Ordinal))
                {
                    lines.RemoveAt(lines.Count - 1);
                }
                text = string.Join("\n", lines).Trim();
            }

            var firstLine = text.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim();
            if (string.IsNullOrWhiteSpace(firstLine)) return string.Empty;

            firstLine = TrimWrapper(firstLine!, '"');
            firstLine = TrimWrapper(firstLine, '\'');
            firstLine = TrimWrapper(firstLine, '`');

            if (!HasAllowedPrefix(firstLine))
            {
                firstLine = "chore: " + firstLine;
            }

            if (firstLine.Length > MaxLength)
            {
                firstLine = firstLine.Substring(0, MaxLength).TrimEnd();
            }

            return firstLine;
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

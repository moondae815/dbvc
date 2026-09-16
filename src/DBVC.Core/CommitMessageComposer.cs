using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DBVC.Core.Models;

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
        /// 형식을 강제하는 자리. 설명만으로는 준수율이 잘 오르지 않아 예시를 함께 싣는다.
        /// </summary>
        public const string SystemPrompt =
            "당신은 SQL Server 스키마 저장소의 커밋 메시지를 짓는다.\n" +
            "규칙:\n" +
            "- 한국어 한 줄로만 답한다. 설명·머리말·코드펜스를 쓰지 않는다.\n" +
            "- 형식은 `접두어: 본문`이다. 접두어는 feat, fix, refactor, perf, chore 중 하나다.\n" +
            "- 스코프(괄호)를 쓰지 않는다.\n" +
            "- 본문은 명령형 현재시제로 끝낸다(예: ~한다, ~더한다, ~고친다).\n" +
            "- 전체 길이는 72자를 넘지 않는다. 마침표로 끝내지 않는다.\n" +
            "예시:\n" +
            "feat: 주문 조회 프로시저에 취소일자 필터를 더한다\n" +
            "fix: 재고 계산 함수의 반올림 오류를 고친다\n" +
            "chore: 사용하지 않는 임시 테이블을 지운다";

        /// <summary>
        /// 변경 목록과 diff를 담은 user 메시지를 만든다.
        ///
        /// <paramref name="maxDiffLines"/>를 넘으면 diff는 객체별로 균등하게 잘리지만
        /// <b>목록은 전부 남는다</b> — 잘린 diff로도 무엇이 바뀌었는지는 말할 수 있어야 한다.
        /// </summary>
        public static string BuildUserMessage(IReadOnlyList<DiffFileChange> changes, int maxDiffLines)
        {
            if (changes == null || changes.Count == 0) return string.Empty;

            var builder = new StringBuilder();
            builder.AppendLine("다음은 이번 커밋에 담기는 데이터베이스 객체의 변경이다.");
            builder.AppendLine();
            builder.AppendLine("[변경 객체]");
            foreach (var change in changes)
            {
                builder.AppendLine($"- {Describe(change.RelativePath)} ({StateText(change.Status)})");
            }

            // 0으로 나누지 않도록 최소 1줄은 준다. 상한이 객체 수보다 작은 경우다.
            var perFile = Math.Max(1, maxDiffLines / changes.Count);

            builder.AppendLine();
            builder.AppendLine("[변경 내용(unified diff)]");
            foreach (var change in changes)
            {
                builder.AppendLine($"--- {change.RelativePath}");
                builder.AppendLine(TrimPatch(change.Patch, perFile));
            }

            builder.AppendLine();
            builder.AppendLine("위 변경을 요약한 커밋 메시지 한 줄을 규칙대로 답한다.");
            return builder.ToString();
        }

        /// <summary>
        /// 저장소 경로를 <c>스키마.객체명</c>으로 옮긴다. 규약 밖 경로는 원문을 그대로 쓴다 —
        /// 조용히 버리면 AI가 변경 하나를 통째로 보지 못한다.
        /// </summary>
        private static string Describe(string relativePath)
        {
            if (ObjectPathConvention.TryParseRelativePath(relativePath, out var schema, out var objectType, out var objectName))
            {
                return $"{schema}.{objectName} ({objectType})";
            }
            return relativePath;
        }

        private static string StateText(string? status) => status switch
        {
            "Added" => "추가",
            "Modified" => "수정",
            "Deleted" => "삭제",
            _ => status ?? string.Empty,
        };

        private static string TrimPatch(string patch, int maxLines)
        {
            if (string.IsNullOrEmpty(patch)) return string.Empty;

            var lines = patch.Replace("\r\n", "\n").Split('\n');
            if (lines.Length <= maxLines) return patch;

            return string.Join("\n", lines.Take(maxLines)) + "\n-- (이하 생략)";
        }

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

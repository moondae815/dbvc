using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// SQL 에디터에서 선택된 텍스트를 객체 식별자로 해석하고,
    /// 현재 변경 목록에서 대응하는 항목을 찾는다. (Feature 11, 12)
    /// </summary>
    public static class ObjectNameResolver
    {
        private static readonly char[] TrimmablePunctuation = { ' ', '\t', ';', ',', '(', ')', '\r', '\n' };

        /// <summary>
        /// 선택 텍스트가 단일 식별자면 스키마와 객체명으로 분해한다.
        /// 문장 전체처럼 식별자가 아닌 입력은 추측하지 않고 실패로 처리한다.
        /// </summary>
        public static bool TryParse(string? selection, out string? schema, out string name)
        {
            schema = null;
            name = string.Empty;

            if (string.IsNullOrWhiteSpace(selection)) return false;

            var trimmed = selection!.Trim(TrimmablePunctuation);
            if (trimmed.Length == 0) return false;

            var parts = SplitIdentifier(trimmed);
            if (parts == null || parts.Count == 0) return false;

            // 대괄호 밖에 공백이 있으면 단일 식별자가 아니다.
            if (parts.Any(p => p.Length == 0)) return false;

            var objectName = parts[parts.Count - 1];
            if (objectName.Length == 0) return false;

            name = objectName;
            if (parts.Count >= 2)
            {
                schema = parts[parts.Count - 2];
            }
            return true;
        }

        /// <summary>
        /// 점(.)으로 분리하되 대괄호 안의 점은 이름의 일부로 취급한다.
        /// 대괄호 밖에서 공백을 만나면 식별자가 아니라고 판단해 null을 반환한다.
        /// </summary>
        private static List<string>? SplitIdentifier(string text)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            bool inBrackets = false;

            foreach (var c in text)
            {
                if (c == '[' && !inBrackets)
                {
                    inBrackets = true;
                    continue;
                }
                if (c == ']' && inBrackets)
                {
                    inBrackets = false;
                    continue;
                }

                if (!inBrackets)
                {
                    if (char.IsWhiteSpace(c)) return null;
                    if (c == '.')
                    {
                        parts.Add(current.ToString());
                        current.Clear();
                        continue;
                    }
                }

                current.Append(c);
            }

            if (inBrackets) return null;
            parts.Add(current.ToString());
            return parts;
        }

        /// <summary>
        /// 변경 목록에서 해당 객체를 찾는다.
        /// 스키마가 지정되지 않으면 <c>dbo</c>를 우선하고, 없으면 이름이 일치하는 첫 항목을 쓴다.
        /// </summary>
        public static ChangeRecord? FindMatch(IEnumerable<ChangeRecord>? changes, string? schema, string name)
        {
            if (changes == null || string.IsNullOrWhiteSpace(name)) return null;

            var candidates = changes
                .Where(c => c != null && string.Equals(c.ObjectName, name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 0) return null;

            if (!string.IsNullOrWhiteSpace(schema))
            {
                return candidates.FirstOrDefault(c => string.Equals(c.Schema, schema, StringComparison.OrdinalIgnoreCase));
            }

            return candidates.FirstOrDefault(c => string.Equals(c.Schema, ObjectPathConvention.DefaultSchema, StringComparison.OrdinalIgnoreCase))
                ?? candidates[0];
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// 운영 병합에 딸려 가는 남의 변경을 줄 단위 겹침으로 찾는다(경고 A, 스펙 3.4).
    ///
    /// 원래 설계의 P@develop != P@master는 쓰지 않는다 - 테스트를 거친 브랜치는 develop에 자기
    /// 변경이 들어 있어 운영 병합마다 모든 객체에서 뜬다. 겹침은 방향을 말하지 못하므로 결과는
    /// 판정이 아니라 "사람이 확인할 것"이다.
    /// </summary>
    public static class PromotionLeakDetector
    {
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

        // 어느 객체에나 나오는 줄이다. 빼지 않으면 모든 프로시저가 모든 브랜치와 겹친다.
        private static readonly HashSet<string> Trivial = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GO", "BEGIN", "END", "AS", "(", ")", ",", ";"
        };

        public static IReadOnlyList<PromotionLeak> Detect(
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> source,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>> others)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (others == null) throw new ArgumentNullException(nameof(others));

            var leaks = new List<PromotionLeak>();

            foreach (var path in source.Keys.OrderBy(p => p, StringComparer.Ordinal))
            {
                var sourceLines = NormalizeAll(source[path]);
                if (sourceLines.Count == 0) continue;

                foreach (var branch in others.Keys.OrderBy(b => b, StringComparer.Ordinal))
                {
                    if (!others[branch].TryGetValue(path, out var branchLines)) continue;

                    var overlap = NormalizeAll(branchLines);
                    overlap.IntersectWith(sourceLines);
                    if (overlap.Count == 0) continue;

                    leaks.Add(new PromotionLeak
                    {
                        Path = path,
                        BranchName = branch,
                        Lines = overlap.OrderBy(l => l, StringComparer.Ordinal).ToList()
                    });
                }
            }

            return leaks;
        }

        /// <summary>비교용 형태. 비교에서 뺄 줄이면 null이다.</summary>
        public static string? Normalize(string line)
        {
            if (line == null) return null;
            var collapsed = Whitespace.Replace(line.Trim(), " ");
            if (collapsed.Length == 0 || Trivial.Contains(collapsed)) return null;
            return collapsed;
        }

        private static HashSet<string> NormalizeAll(IEnumerable<string> lines)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                var normalized = Normalize(line);
                if (normalized != null) set.Add(normalized);
            }
            return set;
        }
    }
}

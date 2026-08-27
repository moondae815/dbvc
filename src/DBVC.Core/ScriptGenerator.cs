using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// 여러 객체의 DDL 조각을 하나의 배포/롤백 스크립트로 병합한다. (Feature 8, 9)
    /// 순수 함수이므로 DB·Git·파일 시스템에 접근하지 않는다.
    /// </summary>
    public static class ScriptGenerator
    {
        private const string BatchSeparator = "GO";

        /// <summary>
        /// 섹션들을 정해진 순서로 병합해 단일 스크립트 텍스트를 만든다.
        /// 내용이 빈 섹션은 제외되며 헤더의 개수에도 반영되지 않는다.
        /// <paramref name="exclusions"/>는 호출자(<see cref="ScriptExporter"/>)가 판정한 제외 목록이며,
        /// 파일을 나중에 열어 볼 사람이 무엇이 빠졌는지 알 수 있도록 헤더에 남긴다.
        /// </summary>
        public static string BuildScript(
            IEnumerable<ScriptSection>? sections,
            ScriptKind kind,
            DateTimeOffset generatedAt,
            IReadOnlyCollection<ScriptExclusion>? exclusions = null)
        {
            var ordered = (sections ?? Enumerable.Empty<ScriptSection>())
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Sql))
                .OrderBy(GetTypeSortOrder)
                .ThenBy(s => s.QualifiedName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var builder = new StringBuilder();
            AppendHeader(builder, kind, generatedAt, ordered.Count, exclusions);

            foreach (var section in ordered)
            {
                AppendSection(builder, section);
            }

            return builder.ToString();
        }

        private static void AppendHeader(
            StringBuilder builder,
            ScriptKind kind,
            DateTimeOffset generatedAt,
            int objectCount,
            IReadOnlyCollection<ScriptExclusion>? exclusions)
        {
            var title = kind == ScriptKind.Rollback ? "DBVC 롤백 스크립트" : "DBVC 배포 스크립트";

            builder.AppendLine("/* ============================================================");
            builder.AppendLine($"   {title}");
            builder.AppendLine($"   생성: {generatedAt:yyyy-MM-ddTHH:mm:sszzz}");
            builder.AppendLine($"   객체: {objectCount}");

            AppendExclusions(builder, exclusions);

            builder.AppendLine("   ============================================================ */");
            builder.AppendLine();
        }

        /// <summary>
        /// 사유별로 묶어 적는다. 뭉뚱그리면 무엇을 손으로 해야 하는지 알 수 없다 —
        /// "수동 변경"은 사용자가 ALTER를 써야 한다는 뜻이고 "확인 필요"는 그렇지 않다.
        /// </summary>
        private static void AppendExclusions(StringBuilder builder, IReadOnlyCollection<ScriptExclusion>? exclusions)
        {
            if (exclusions == null || exclusions.Count == 0) return;

            foreach (ScriptExclusionReason reason in Enum.GetValues(typeof(ScriptExclusionReason)))
            {
                var names = exclusions
                    .Where(e => e != null && e.Reason == reason)
                    .Select(e => e.QualifiedName)
                    .ToList();

                if (names.Count == 0) continue;

                builder.AppendLine($"   제외 — {DescribeReason(reason)}: {names.Count} ({string.Join(", ", names)})");
            }
        }

        private static string DescribeReason(ScriptExclusionReason reason)
        {
            switch (reason)
            {
                case ScriptExclusionReason.NoContent:
                    return "스크립트로 만들 내용이 없습니다";
                case ScriptExclusionReason.ManualChangeRequired:
                    return "대상에 이미 있어 수동 변경이 필요합니다";
                case ScriptExclusionReason.NotInBranch:
                    return "브랜치에 없어 확인이 필요합니다";
                default:
                    throw new InvalidOperationException($"처리되지 않은 {nameof(ScriptExclusionReason)}: {reason}");
            }
        }

        private static void AppendSection(StringBuilder builder, ScriptSection section)
        {
            builder.AppendLine($"/* ---- {section.QualifiedName} ({section.RelativePath}) ---- */");

            var sql = section.Sql!.TrimEnd();
            builder.AppendLine(sql);

            // 원본이 이미 GO로 끝나면 배치 구분자를 중복해서 넣지 않는다.
            if (!EndsWithBatchSeparator(sql))
            {
                builder.AppendLine(BatchSeparator);
            }

            builder.AppendLine();
        }

        private static bool EndsWithBatchSeparator(string sql)
        {
            var lastLine = sql.Split('\n').LastOrDefault();
            return string.Equals(lastLine?.Trim(), BatchSeparator, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 의존성 해석이 아니라 관례적 배치 순서다. 설계 3.3 참고.
        /// </summary>
        private static int GetTypeSortOrder(ScriptSection section)
        {
            ObjectPathConvention.TryParseRelativePath(section.RelativePath, out _, out var objectType, out _);
            return ObjectPathConvention.GetTypeSortOrder(objectType);
        }
    }
}

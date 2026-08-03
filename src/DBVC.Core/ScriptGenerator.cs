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
        /// <paramref name="excludedObjects"/>는 호출자(<see cref="ScriptExporter"/>)가 판정한 제외 목록이며,
        /// 파일을 나중에 열어 볼 사람이 무엇이 빠졌는지 알 수 있도록 헤더에 남긴다.
        /// </summary>
        public static string BuildScript(
            IEnumerable<ScriptSection>? sections,
            ScriptKind kind,
            DateTimeOffset generatedAt,
            IReadOnlyCollection<string>? excludedObjects = null)
        {
            var ordered = (sections ?? Enumerable.Empty<ScriptSection>())
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Sql))
                .OrderBy(GetTypeSortOrder)
                .ThenBy(s => s.QualifiedName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var builder = new StringBuilder();
            AppendHeader(builder, kind, generatedAt, ordered.Count, excludedObjects);

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
            IReadOnlyCollection<string>? excludedObjects)
        {
            var title = kind == ScriptKind.Rollback ? "DBVC Rollback Script" : "DBVC Deployment Script";

            builder.AppendLine("/* ============================================================");
            builder.AppendLine($"   {title}");
            builder.AppendLine($"   Generated: {generatedAt:yyyy-MM-ddTHH:mm:sszzz}");
            builder.AppendLine($"   Objects: {objectCount}");

            if (excludedObjects != null && excludedObjects.Count > 0)
            {
                builder.AppendLine($"   Excluded: {excludedObjects.Count} ({string.Join(", ", excludedObjects)})");
            }

            builder.AppendLine("   ============================================================ */");
            builder.AppendLine();
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

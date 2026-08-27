using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// 대상 DB를 훑는 것만으로는 "브랜치에 있는데 DB에 없는 것"을 찾을 수 없다 —
    /// 열거 자체가 DB에서 나오기 때문이다. 그것이 배포에서 가장 중요한 항목이므로
    /// 저장소 쪽에서 따로 찾는다.
    ///
    /// 판정은 파일 시스템에 닿지 않는 순수 함수로 두고 스캔만 어댑터가 한다.
    /// </summary>
    public static class SchemaComparison
    {
        public static IReadOnlyList<SchemaDifference> FindMissingInDatabase(
            IEnumerable<string> repositoryRelativePaths,
            ISet<string> extractedRelativePaths)
        {
            var missing = new List<SchemaDifference>();
            if (repositoryRelativePaths == null) return missing;

            foreach (var raw in repositoryRelativePaths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var normalized = raw.Replace('\\', '/');

                // 규약 밖의 .sql은 객체가 아니다. 저장소에 사람이 둔 메모까지
                // "DB에 없는 객체"로 보고하면 목록이 통째로 신뢰를 잃는다.
                if (!ObjectPathConvention.TryParseRelativePath(normalized, out var schema, out var objectType, out var objectName))
                {
                    continue;
                }

                if (extractedRelativePaths != null && extractedRelativePaths.Contains(normalized)) continue;

                missing.Add(new SchemaDifference(
                    ObjectPathConvention.GetQualifiedName(schema, objectName),
                    normalized,
                    objectType,
                    ObjectDiffState.MissingInDatabase));
            }

            return missing;
        }

        /// <summary>
        /// 저장소의 `.sql`을 슬래시 구분 상대 경로로 모은다. 규약 판정은 하지 않는다 —
        /// 그것은 <see cref="FindMissingInDatabase"/>가 하고, 여기서도 하면 규칙이 두 곳에 생긴다.
        /// </summary>
        public static IReadOnlyList<string> EnumerateRepositoryScriptPaths(string repositoryPath)
        {
            var paths = new List<string>();
            if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath)) return paths;

            try
            {
                var root = repositoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                foreach (var full in Directory.EnumerateFiles(root, "*.sql", SearchOption.AllDirectories))
                {
                    var relative = full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var normalized = relative.Replace('\\', '/');

                    // .git 안에도 .sql이 들어갈 수 있다. Git의 내부 파일은 객체가 아니다.
                    if (normalized.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)) continue;

                    paths.Add(normalized);
                }
            }
            catch (Exception ex)
            {
                // 권한 문제로 일부를 못 읽는 것이 검사 전체를 죽이면 안 된다.
                // 다만 목록이 줄면 "브랜치에만 있음"이 빠지므로 흔적은 남긴다.
                Debug.WriteLine($"SchemaComparison.EnumerateRepositoryScriptPaths failed for '{repositoryPath}': {ex.Message}");
            }

            return paths;
        }
    }
}

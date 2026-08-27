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
            IEnumerable<string>? repositoryRelativePaths,
            ISet<string>? extractedRelativePaths)
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
        ///
        /// 스캔이 끝까지 돌았는지를 함께 돌려주는 <see cref="ScanRepositoryScriptPaths"/>를 쓴다.
        /// 이 오버로드는 완결 여부가 필요 없는 자리(경로 목록만 보는 검사)를 위해 남긴다.
        /// </summary>
        public static IReadOnlyList<string> EnumerateRepositoryScriptPaths(string repositoryPath)
        {
            return ScanRepositoryScriptPaths(repositoryPath).Paths;
        }

        /// <summary>
        /// <see cref="EnumerateRepositoryScriptPaths"/>와 같되 스캔이 끝까지 돌았는지를 함께 돌려준다.
        ///
        /// <b>부분 목록은 조용히 넘길 수 없다.</b> <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>은
        /// 권한이 없거나 잠긴 폴더를 하나 만나면 그 지점에서 순회 전체를 중단한다. 그러면 그
        /// 아래의 "브랜치에만 있는 객체"가 통째로 사라지고, 다른 차이가 없으면 화면은
        /// "브랜치와 일치합니다"라고 말한다 — 이 기능이 존재하는 이유가 바로 그 문장을
        /// 믿을 수 있게 만드는 것이다. Debug.WriteLine은 Release에서 사라지므로 흔적도 없다.
        /// </summary>
        public static RepositoryScanResult ScanRepositoryScriptPaths(string repositoryPath)
        {
            var paths = new List<string>();

            // 경로가 없거나 폴더가 사라졌다면 브랜치의 내용을 하나도 읽지 못한 것이다.
            // 그것을 "차이 없음"으로 보고하면 가장 위험한 거짓말이 된다.
            if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            {
                return new RepositoryScanResult(paths, isComplete: false);
            }

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
                // 권한 문제로 일부를 못 읽는 것이 검사 전체를 죽이면 안 된다. 모은 것까지는
                // 그대로 쓰되, 결과가 일부라는 사실을 호출자에게 반드시 알린다.
                Debug.WriteLine($"SchemaComparison.ScanRepositoryScriptPaths failed for '{repositoryPath}': {ex.Message}");
                return new RepositoryScanResult(paths, isComplete: false);
            }

            return new RepositoryScanResult(paths, isComplete: true);
        }
    }

    /// <summary>
    /// 저장소 스캔 한 번의 결과. 목록만으로는 "브랜치에 아무것도 없다"와 "브랜치를 다 읽지
    /// 못했다"를 구분할 수 없어, 화면이 후자를 "일치합니다"로 옮기게 된다.
    /// </summary>
    public class RepositoryScanResult
    {
        public RepositoryScanResult(IReadOnlyList<string> paths, bool isComplete)
        {
            Paths = paths;
            IsComplete = isComplete;
        }

        public IReadOnlyList<string> Paths { get; }

        /// <summary><c>false</c>면 <see cref="Paths"/>는 저장소의 일부다.</summary>
        public bool IsComplete { get; }
    }
}

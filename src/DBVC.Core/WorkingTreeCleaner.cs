using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// DDL 로그가 DROP을 기록한 객체의 <c>.sql</c> 파일을 작업 트리에서 제거한다.
    /// SmoManager는 존재하는 객체만 추출하므로 사라진 객체의 파일은 아무도 지우지 않는다.
    /// 파일이 남으면 Git이 삭제를 감지하지 못해 커밋되지 않는다.
    /// </summary>
    public class WorkingTreeCleaner : IWorkingTreeCleaner
    {
        private const string DeletedState = "Deleted";

        public CleanupResult RemoveDeletedObjectFiles(string repoPath, IEnumerable<ChangeRecord> records)
        {
            var result = new CleanupResult();

            if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath)) return result;

            var repoRoot = Path.GetFullPath(repoPath);

            foreach (var record in records ?? Enumerable.Empty<ChangeRecord>())
            {
                var fullPath = ResolveDeletableFile(repoRoot, record);
                if (fullPath == null) continue;

                try
                {
                    File.Delete(fullPath);
                    result.RemovedPaths.Add(record.RelativePath);
                }
                catch (Exception ex)
                {
                    // 파일 하나의 실패가 나머지 정리를 막아서는 안 된다. (SmoManager.ScriptAll과 같은 방침)
                    Debug.WriteLine($"WorkingTreeCleaner failed to delete '{record.RelativePath}': {ex.Message}");
                    result.FailedPaths.Add(record.RelativePath);
                }
            }

            return result;
        }

        /// <summary>
        /// 삭제해도 되는 파일이면 절대 경로를, 아니면 <c>null</c>을 반환한다.
        /// </summary>
        private static string? ResolveDeletableFile(string repoRoot, ChangeRecord? record)
        {
            if (record == null) return null;
            if (!string.Equals(record.State, DeletedState, StringComparison.OrdinalIgnoreCase)) return null;

            // DDL 로그에 근거가 있는 항목만 지운다.
            // LastLogId가 0이면 Git 상태에서만 유래한 항목이고, 그건 이미 파일이 없다는 뜻이다.
            if (record.LastLogId <= 0) return null;

            // DBVC의 경로 규약을 따르지 않는 파일은 DBVC가 만든 것이 아니다.
            if (!ObjectPathConvention.TryParseRelativePath(record.RelativePath, out _, out _, out _)) return null;

            var combined = Path.GetFullPath(
                Path.Combine(repoRoot, record.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

            // ".." 세 조각도 경로 규약 검사는 통과하므로 루트 검사가 마지막 방어선이다.
            if (!IsUnder(repoRoot, combined)) return null;

            return File.Exists(combined) ? combined : null;
        }

        private static bool IsUnder(string root, string candidate)
        {
            var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 작업 트리 정리 결과. 실패한 경로는 사용자에게 알려야 한다.
    /// </summary>
    public class CleanupResult
    {
        public List<string> RemovedPaths { get; } = new List<string>();
        public List<string> FailedPaths { get; } = new List<string>();
        public bool HasFailures => FailedPaths.Count > 0;
    }
}

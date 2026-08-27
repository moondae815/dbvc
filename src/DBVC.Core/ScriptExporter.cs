using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// 선택된 객체들의 DDL을 모아 단일 배포/롤백 스크립트를 만든다. (Feature 8, 9)
    /// </summary>
    public class ScriptExporter
    {
        private readonly IConfigManager _configManager;
        private readonly IGitManager _gitManager;

        public ScriptExporter(IConfigManager configManager, IGitManager gitManager)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _gitManager = gitManager ?? throw new ArgumentNullException(nameof(gitManager));
        }

        public ScriptExportResult Export(
            string serverName,
            string databaseName,
            IEnumerable<ChangeRecord> targets,
            ScriptKind kind,
            DateTimeOffset generatedAt)
        {
            var result = new ScriptExportResult();

            var mapping = _configManager.TryGetMapping(serverName, databaseName);
            if (mapping == null)
            {
                Debug.WriteLine($"'{serverName}.{databaseName}'에 매핑된 Git 저장소가 없어 스크립트를 생성할 수 없습니다.");
                return result;
            }

            var sections = new List<ScriptSection>();

            foreach (var target in targets ?? Enumerable.Empty<ChangeRecord>())
            {
                if (target == null || string.IsNullOrWhiteSpace(target.RelativePath)) continue;

                var sql = kind == ScriptKind.Rollback
                    ? _gitManager.GetFileContentBeforeLastCommit(serverName, databaseName, target.RelativePath)
                    : ReadWorkingTreeFile(mapping.GitPath, target.RelativePath);

                if (string.IsNullOrWhiteSpace(sql))
                {
                    // 되돌릴 이전 리비전이 없거나 추출된 파일이 없는 경우다. 오류가 아니라 제외 대상이다.
                    result.ExcludedObjects.Add(new ScriptExclusion(target.QualifiedName, ScriptExclusionReason.NoContent));
                    continue;
                }

                sections.Add(new ScriptSection
                {
                    QualifiedName = target.QualifiedName,
                    RelativePath = target.RelativePath,
                    Sql = sql
                });
            }

            result.IncludedCount = sections.Count;
            result.Script = sections.Count > 0
                ? ScriptGenerator.BuildScript(sections, kind, generatedAt, result.ExcludedObjects)
                : string.Empty;

            return result;
        }

        /// <summary>
        /// 차이 검사 결과에서 배포 스크립트를 만든다.
        ///
        /// 재료는 <b>브랜치의 파일</b>이지 대상 DB에서 다시 뜬 것이 아니다. "develop에 병합된
        /// 것만 테스트에 나간다"를 검사가 아니라 배치로 지킨다 — 배포 클론은 develop에
        /// 고정되어 있고 병합 안 된 변경은 애초에 파일로 존재하지 않는다.
        /// </summary>
        public ScriptExportResult ExportFromComparison(
            string serverName,
            string databaseName,
            IEnumerable<SchemaDifference>? differences,
            DateTimeOffset generatedAt)
        {
            var result = new ScriptExportResult();

            var mapping = _configManager.TryGetMapping(serverName, databaseName);
            if (mapping == null)
            {
                Debug.WriteLine($"'{serverName}.{databaseName}'에 매핑된 Git 저장소가 없어 스크립트를 생성할 수 없습니다.");
                return result;
            }

            var sections = new List<ScriptSection>();

            foreach (var difference in differences ?? Enumerable.Empty<SchemaDifference>())
            {
                if (difference == null || string.IsNullOrWhiteSpace(difference.RelativePath)) continue;

                var disposition = DeploymentClassifier.Classify(difference.State, difference.ObjectType);

                if (disposition == ScriptDisposition.ExcludeManualChange)
                {
                    result.ExcludedObjects.Add(new ScriptExclusion(difference.QualifiedName, ScriptExclusionReason.ManualChangeRequired));
                    continue;
                }

                if (disposition == ScriptDisposition.ExcludeNotInBranch)
                {
                    result.ExcludedObjects.Add(new ScriptExclusion(difference.QualifiedName, ScriptExclusionReason.NotInBranch));
                    continue;
                }

                var sql = ReadWorkingTreeFile(mapping.GitPath, difference.RelativePath);
                if (string.IsNullOrWhiteSpace(sql))
                {
                    // 검사할 때는 있었는데 지금 없다. 조용히 빼면 배포가 덜 된 채로 성공한 척한다.
                    result.ExcludedObjects.Add(new ScriptExclusion(difference.QualifiedName, ScriptExclusionReason.NoContent));
                    continue;
                }

                sections.Add(new ScriptSection
                {
                    QualifiedName = difference.QualifiedName,
                    RelativePath = difference.RelativePath,
                    Sql = sql
                });
            }

            result.IncludedCount = sections.Count;
            result.Script = sections.Count > 0
                ? ScriptGenerator.BuildScript(sections, ScriptKind.Deployment, generatedAt, result.ExcludedObjects)
                : string.Empty;

            return result;
        }

        private static string? ReadWorkingTreeFile(string repoPath, string relativePath)
        {
            try
            {
                var fullPath = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ScriptExporter.ReadWorkingTreeFile failed for '{relativePath}': {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// 스크립트 생성 결과. 제외된 객체는 사용자에게 알려야 한다.
    /// </summary>
    public class ScriptExportResult
    {
        public string Script { get; set; } = string.Empty;
        public int IncludedCount { get; set; }

        /// <summary>제외된 객체와 사유. 사용자가 할 일이 사유마다 다르다.</summary>
        public List<ScriptExclusion> ExcludedObjects { get; } = new List<ScriptExclusion>();

        /// <summary>파일로 저장할 내용이 있는지.</summary>
        public bool HasContent => IncludedCount > 0 && !string.IsNullOrWhiteSpace(Script);
    }
}

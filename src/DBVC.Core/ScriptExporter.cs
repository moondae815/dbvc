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
                    result.ExcludedObjects.Add(target.QualifiedName);
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
        public List<string> ExcludedObjects { get; } = new List<string>();

        /// <summary>파일로 저장할 내용이 있는지.</summary>
        public bool HasContent => IncludedCount > 0 && !string.IsNullOrWhiteSpace(Script);
    }
}

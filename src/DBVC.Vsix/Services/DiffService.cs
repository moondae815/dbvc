using System;
using System.Diagnostics;
using System.IO;
using DBVC.Core;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// Git HEAD 버전(좌측)과 현재 데이터베이스 버전(우측)의 Side-by-Side Diff를 만든다.
    /// 우측은 새로고침 시 SMO가 작업 트리에 추출해 둔 파일에서 읽는다.
    /// </summary>
    public class DiffService
    {
        private readonly IConfigManager? _configManager;
        private readonly IGitManager? _gitManager;

        public DiffService()
        {
        }

        public DiffService(IConfigManager configManager, IGitManager gitManager)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _gitManager = gitManager ?? throw new ArgumentNullException(nameof(gitManager));
        }

        public SideBySideDiffModel GetDiffModelFromString(string? oldText, string? newText)
        {
            return SideBySideDiffBuilder.Diff(oldText ?? string.Empty, newText ?? string.Empty);
        }

        /// <summary>
        /// 객체 하나의 Diff 모델을 만든다.
        /// 신규 객체는 좌측이, 삭제된 객체는 우측이 비어 있게 된다.
        /// </summary>
        public SideBySideDiffModel GetDiffModel(string serverName, string databaseName, string? relativePath)
        {
            var (oldText, newText) = GetDiffTexts(serverName, databaseName, relativePath);
            return GetDiffModelFromString(oldText, newText);
        }

        /// <summary>
        /// Diff 양쪽 원문을 반환한다. AvalonEdit 에디터에 그대로 넣기 위한 용도.
        /// </summary>
        public (string OldText, string NewText) GetDiffTexts(string serverName, string databaseName, string? relativePath)
        {
            if (_configManager == null || _gitManager == null || string.IsNullOrWhiteSpace(relativePath))
            {
                return (string.Empty, string.Empty);
            }

            var mapping = _configManager.TryGetMapping(serverName, databaseName);
            if (mapping == null)
            {
                return (string.Empty, string.Empty);
            }

            var oldText = _gitManager.GetFileContentAtHead(serverName, databaseName, relativePath!) ?? string.Empty;
            var newText = ReadWorkingTreeFile(mapping.GitPath, relativePath!);

            return (oldText, newText);
        }

        private static string ReadWorkingTreeFile(string repoPath, string relativePath)
        {
            try
            {
                var fullPath = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                // 객체가 DROP되면 파일이 없다. 이 경우 우측을 비워 삭제를 표현한다.
                return File.Exists(fullPath) ? File.ReadAllText(fullPath) : string.Empty;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DiffService.ReadWorkingTreeFile failed for '{relativePath}': {ex.Message}");
                return string.Empty;
            }
        }
    }
}

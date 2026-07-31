using DBVC.Core;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace DBVC.Vsix.Services
{
    public class DiffService
    {
        private readonly GitManager? _gitManager;
        private readonly SmoManager? _smoManager;

        public DiffService(GitManager? gitManager = null, SmoManager? smoManager = null)
        {
            _gitManager = gitManager;
            _smoManager = smoManager;
        }

        public SideBySideDiffModel GetDiffModelFromString(string? oldText, string? newText)
        {
            return SideBySideDiffBuilder.Diff(oldText ?? "", newText ?? "");
        }

        public SideBySideDiffModel GetDiffModel(string objectName)
        {
            // Currently returns diff between empty strings unless old/new text is resolved
            return GetDiffModelFromString("", "");
        }
    }
}

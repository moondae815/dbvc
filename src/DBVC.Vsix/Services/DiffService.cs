using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace DBVC.Vsix.Services
{
    public class DiffService
    {
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

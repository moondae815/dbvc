using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace DBVC.Vsix.UI
{
    [Guid("d3b4e6d4-5c9f-4b7d-8e4d-7a6c5b4d3e2f")]
    public class ViewChangesToolWindow : ToolWindowPane
    {
        public ViewChangesToolWindow() : base(null)
        {
            this.Caption = "DBVC View Changes";
            this.Content = new ViewChangesControl();
        }
    }
}

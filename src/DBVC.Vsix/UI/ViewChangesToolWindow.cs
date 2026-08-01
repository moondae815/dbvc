using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace DBVC.Vsix.UI
{
    [Guid("d3b4e6d4-5c9f-4b7d-8e4d-7a6c5b4d3e2f")]
    public class ViewChangesToolWindow : ToolWindowPane
    {
        public ViewChangesToolWindow() : base(null)
        {
            Caption = "DBVC View Changes";

            // ToolWindowPane은 VS 셸이 매개변수 없이 생성하므로 공유 인스턴스를 사용한다.
            var services = DbvcServices.Default;
            Content = new ViewChangesControl(services.CreateViewChangesViewModel(), services.CreateDiffService());
        }
    }
}

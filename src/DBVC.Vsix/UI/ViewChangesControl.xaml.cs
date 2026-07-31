using System.Windows.Controls;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.UI
{
    public partial class ViewChangesControl : UserControl
    {
        public ViewChangesControl()
        {
            InitializeComponent();
            this.DataContext = new ViewChangesViewModel();
        }
    }
}

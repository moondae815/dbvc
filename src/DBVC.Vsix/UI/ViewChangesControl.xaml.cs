using System.Windows.Controls;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.UI
{
    public partial class ViewChangesControl : UserControl
    {
        public ViewChangesControl() : this(new ViewChangesViewModel())
        {
        }

        public ViewChangesControl(ViewChangesViewModel viewModel)
        {
            InitializeComponent();
            this.DataContext = viewModel;
        }
    }
}

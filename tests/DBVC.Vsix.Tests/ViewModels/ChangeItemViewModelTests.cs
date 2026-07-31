using NUnit.Framework;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.ViewModels
{
    [TestFixture]
    public class ChangeItemViewModelTests
    {
        [Test]
        public void Properties_CanBeSetAndRetrieved()
        {
            var item = new ChangeItemViewModel
            {
                IsSelected = true,
                ObjectName = "dbo.TestTable",
                State = "Modified"
            };

            Assert.That(item.IsSelected, Is.True);
            Assert.That(item.ObjectName, Is.EqualTo("dbo.TestTable"));
            Assert.That(item.State, Is.EqualTo("Modified"));
        }

        [Test]
        public void IsSelected_RaisesPropertyChanged()
        {
            var item = new ChangeItemViewModel();
            bool raised = false;
            item.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ChangeItemViewModel.IsSelected))
                {
                    raised = true;
                }
            };

            item.IsSelected = true;

            Assert.That(raised, Is.True);
        }
    }
}

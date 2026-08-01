using NUnit.Framework;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.ViewModels
{
    [TestFixture]
    public class ViewChangesViewModelTests
    {
        [Test]
        public void CommitMessage_CanBeSetAndRetrieved()
        {
            var vm = new ViewChangesViewModel();
            vm.CommitMessage = "Test commit";
            Assert.That(vm.CommitMessage, Is.EqualTo("Test commit"));
        }

        [Test]
        public void Refresh_PopulatesChangesList()
        {
            var vm = new ViewChangesViewModel();
            Assert.That(vm.Changes, Is.Not.Null);
            Assert.That(vm.Changes.Count, Is.EqualTo(0));
        }

        [Test]
        public void Refresh_ClearsExistingChanges()
        {
            var vm = new ViewChangesViewModel();
            vm.Changes.Add(new ChangeItemViewModel { ObjectName = "dbo.Table1", State = "Modified" });
            Assert.That(vm.Changes.Count, Is.EqualTo(1));

            vm.Refresh();

            Assert.That(vm.Changes.Count, Is.EqualTo(0));
        }

        [Test]
        public void RefreshCommand_IsNotNullAndExecutesRefresh()
        {
            var vm = new ViewChangesViewModel();
            vm.Changes.Add(new ChangeItemViewModel { ObjectName = "dbo.Table1", State = "Modified" });
            Assert.That(vm.RefreshCommand, Is.Not.Null);
            Assert.That(vm.RefreshCommand.CanExecute(null), Is.True);

            vm.RefreshCommand.Execute(null);

            Assert.That(vm.Changes.Count, Is.EqualTo(0));
        }

        [Test]
        public void SelectedChange_CanBeSetAndRetrieved()
        {
            var vm = new ViewChangesViewModel();
            var item = new ChangeItemViewModel { ObjectName = "dbo.Table1", State = "Modified" };
            
            vm.SelectedChange = item;

            Assert.That(vm.SelectedChange, Is.EqualTo(item));
        }

        [Test]
        public void IsInitialized_DefaultsToFalse()
        {
            var vm = new ViewChangesViewModel();
            Assert.That(vm.IsInitialized, Is.False);
        }

        [Test]
        public void SetupCommand_SetsIsInitializedToTrue()
        {
            var vm = new ViewChangesViewModel();
            vm.SetupCommand.Execute(null);
            Assert.That(vm.IsInitialized, Is.True);
        }
    }
}

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
    }
}

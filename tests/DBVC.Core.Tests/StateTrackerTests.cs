using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class StateTrackerTests
    {
        [Test]
        public void GetPendingChanges_ReturnsList()
        {
            var tracker = new StateTracker();
            var changes = tracker.GetPendingChanges("conn");
            Assert.That(changes, Is.Not.Null);
        }
    }
}

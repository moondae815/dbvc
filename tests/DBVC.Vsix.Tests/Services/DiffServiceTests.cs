using NUnit.Framework;
using DBVC.Vsix.Services;

namespace DBVC.Vsix.Tests.Services
{
    [TestFixture]
    public class DiffServiceTests
    {
        [Test]
        public void DiffString_ReturnsModel()
        {
            var diffService = new DiffService();
            var model = diffService.GetDiffModelFromString("A", "B");
            Assert.That(model, Is.Not.Null);
            Assert.That(model.OldText.Lines.Count, Is.EqualTo(1));
        }

        [Test]
        public void GetDiffModel_WithObjectName_ReturnsModel()
        {
            var diffService = new DiffService();
            var model = diffService.GetDiffModel("dbo.Table1");
            Assert.That(model, Is.Not.Null);
        }

        [Test]
        public void GetDiffModelFromString_NullInputs_HandlesNullsGracefully()
        {
            var diffService = new DiffService();
            var model = diffService.GetDiffModelFromString(null, null);
            Assert.That(model, Is.Not.Null);
        }
    }
}

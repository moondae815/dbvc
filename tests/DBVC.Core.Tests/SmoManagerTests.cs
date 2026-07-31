using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class SmoManagerTests
    {
        [Test]
        public void ScriptObjects_GeneratesFile()
        {
            // TODO: Implement full file I/O verification once actual SMO scripting logic is fleshed out.
            var manager = new SmoManager();
            bool result = manager.ScriptObjects("conn", new[] { "urn" }, "out.sql");
            Assert.That(result, Is.True);
        }
    }
}

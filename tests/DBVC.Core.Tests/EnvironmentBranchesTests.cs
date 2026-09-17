using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// master 목록에 develop이 뜨면 develop을 통째로 운영에 병합하는 것이 버튼 한 번이 된다.
    /// </summary>
    [TestFixture]
    public class EnvironmentBranchesTests
    {
        [TestCase("develop", true)]
        [TestCase("master", true)]
        [TestCase("Develop", false)]
        [TestCase("PROJ-123", false)]
        [TestCase("feature/develop", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void IsEnvironmentBranch_MatchesExactNames_WhenNameIsGiven(string? name, bool expected)
        {
            Assert.That(EnvironmentBranches.IsEnvironmentBranch(name), Is.EqualTo(expected));
        }
    }
}

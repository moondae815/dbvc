using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

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

        [TestCase(MappingMode.Deploy, "develop")]
        [TestCase(MappingMode.Audit, "master")]
        public void SuggestFor_NamesTheEnvironmentBranch_WhenTheCloneIsPinned(MappingMode mode, string expected)
        {
            Assert.That(EnvironmentBranches.SuggestFor(mode), Is.EqualTo(expected));
        }

        /// <summary>
        /// 개발 클론에 이름을 채워 주면 칸을 비우지 않은 사람이 2026-09-09에 철회한 "develop 고정"을
        /// 그대로 뒤집어쓴다 - 티켓 브랜치에서 커밋하는 흐름 자체가 막힌다.
        /// </summary>
        [Test]
        public void SuggestFor_ReturnsNull_WhenTheCloneIsForDevelopment()
        {
            Assert.That(EnvironmentBranches.SuggestFor(MappingMode.Write), Is.Null);
        }

        [TestCase(MappingMode.Write, "master", true)]
        [TestCase(MappingMode.Write, "develop", false)]
        [TestCase(MappingMode.Write, "PROJ-123", false)]
        [TestCase(MappingMode.Write, "Master", false)]
        [TestCase(MappingMode.Write, null, false)]
        // 배포·감사는 커밋 자체가 금지라(MappingPolicy) 물을 일이 없다.
        [TestCase(MappingMode.Deploy, "master", false)]
        [TestCase(MappingMode.Audit, "master", false)]
        public void WarnsBeforeCommit_AsksOnlyOnMaster_WhenTheCloneIsForDevelopment(
            MappingMode mode, string? currentBranch, bool expected)
        {
            Assert.That(EnvironmentBranches.WarnsBeforeCommit(mode, currentBranch), Is.EqualTo(expected));
        }
    }
}

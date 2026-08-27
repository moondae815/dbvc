using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 사용자가 붙여 넣는 것은 GitHub·GitLab이 알려주는 Clone URL이다.
    /// 그 문자열에서 폴더 이름을 뽑는 규칙은 네트워크 없이 전량 고정할 수 있다.
    /// </summary>
    [TestFixture]
    public class RemoteUrlNamingTests
    {
        [Test]
        public void SuggestFolderName_ReturnsTheRepositoryName_WhenScpFormUrl()
        {
            Assert.That(RemoteUrlNaming.SuggestFolderName("git@github.com:org/db-schema-sales.git"),
                Is.EqualTo("db-schema-sales"));
        }

        [Test]
        public void SuggestFolderName_ReturnsTheRepositoryName_WhenSshUrlHasPortAndNestedGroups()
        {
            // GitLab이 비표준 SSH 포트를 쓰면 Clone 버튼이 이 형태를 내준다.
            Assert.That(RemoteUrlNaming.SuggestFolderName("ssh://git@gitlab.corp:2222/db/team/db-schema.git"),
                Is.EqualTo("db-schema"));
        }

        [Test]
        public void SuggestFolderName_ReturnsTheRepositoryName_WhenScpFormHasNoPathSeparator()
        {
            Assert.That(RemoteUrlNaming.SuggestFolderName("git@host:db-schema"), Is.EqualTo("db-schema"));
        }

        [Test]
        public void SuggestFolderName_DropsTheGitSuffix_InAnyCase()
        {
            Assert.That(RemoteUrlNaming.SuggestFolderName("git@host:org/Sales.GIT"), Is.EqualTo("Sales"));
        }

        [Test]
        public void SuggestFolderName_IgnoresTrailingSeparators()
        {
            Assert.That(RemoteUrlNaming.SuggestFolderName("ssh://git@host/org/db-schema.git/"),
                Is.EqualTo("db-schema"));
        }

        [Test]
        public void SuggestFolderName_ReturnsNull_WhenUrlIsEmpty()
        {
            Assert.That(RemoteUrlNaming.SuggestFolderName(null), Is.Null);
            Assert.That(RemoteUrlNaming.SuggestFolderName("   "), Is.Null);
        }

        [Test]
        public void SuggestFolderName_ReturnsNull_WhenTheNameWouldNotBeAValidFolderName()
        {
            // 제안을 못 하는 것과 못 만들 이름을 제안하는 것은 다르다.
            // 후자는 사용자가 확인을 누른 뒤에야 실패한다.
            Assert.That(RemoteUrlNaming.SuggestFolderName("git@host:org/a|b.git"), Is.Null);
        }
    }
}

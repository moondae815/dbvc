using DBVC.Core;
using DBVC.Core.Models;
using NUnit.Framework;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class SqlConnectionFactoryTests
    {
        private static SessionCredentialStore NewStore() => new SessionCredentialStore();

        [Test]
        public void Build_UsesWindowsAuth_WhenNoCredentialIsStored()
        {
            // 정상 흐름에서는 Connect가 항상 Set을 부르므로 이 갈래에 닿지 않는다.
            // 남겨 두는 것은 방어다 — 통합 인증으로 한 번 시도하는 편이 예외로 죽는 것보다 낫다.
            var connectionString = new SqlConnectionFactory(NewStore()).Build("srv", "db");

            Assert.That(connectionString, Does.Contain("Integrated Security=True"));
            Assert.That(connectionString, Does.Contain("srv"));
            Assert.That(connectionString, Does.Contain("db"));
        }

        [Test]
        public void Build_UsesWindowsAuth_WhenTheStoredModeIsWindows()
        {
            var store = NewStore();
            store.Set("srv", "db", SqlAuthMode.Windows, null, null);

            var connectionString = new SqlConnectionFactory(store).Build("srv", "db");

            Assert.That(connectionString, Does.Contain("Integrated Security=True"));
        }

        [Test]
        public void Build_UsesTheStoredUserAndPassword_ForSqlAuth()
        {
            var store = NewStore();
            store.Set("srv", "db", SqlAuthMode.Sql, "sa", "p@ss");

            var connectionString = new SqlConnectionFactory(store).Build("srv", "db");

            Assert.That(connectionString, Does.Contain("User ID=sa"));
            Assert.That(connectionString, Does.Contain("p@ss"));
            Assert.That(connectionString, Does.Not.Contain("Integrated Security=True"));
        }

        [Test]
        public void Build_Throws_WhenSqlAuthHasNoPassword()
        {
            var store = NewStore();
            store.Set("srv", "db", SqlAuthMode.Sql, "sa", null);

            var factory = new SqlConnectionFactory(store);

            var ex = Assert.Throws<SqlCredentialException>(() => factory.Build("srv", "db"));
            Assert.That(ex!.Message, Does.Contain("SQL 인증"),
                "영문 원문 대신 한국어 안내가 나와야 합니다");
        }

        [Test]
        public void Build_Throws_WhenSqlAuthHasNoUserName()
        {
            var store = NewStore();
            store.Set("srv", "db", SqlAuthMode.Sql, null, "p@ss");

            Assert.Throws<SqlCredentialException>(() => new SqlConnectionFactory(store).Build("srv", "db"));
        }

        [Test]
        public void Build_PointsAtObjectExplorer_WhenSqlAuthHasNoPassword()
        {
            var store = NewStore();
            store.Set("srv", "db", SqlAuthMode.Sql, "sa", null);

            var ex = Assert.Throws<SqlCredentialException>(() => new SqlConnectionFactory(store).Build("srv", "db"));

            Assert.That(ex!.Message, Does.Contain("개체 탐색기"),
                "이제 인증 정보를 얻는 길이 개체 탐색기뿐이므로 안내가 그리로 보내야 합니다");
            Assert.That(ex.Message, Does.Not.Contain("Windows 계정"),
                "DPAPI가 사라졌으므로 '저장한 Windows 계정에서만 복호화된다'는 안내는 거짓입니다");
        }

        [Test]
        public void BuildSql_DoesNotPersistSecurityInfo()
        {
            // 연결 후 ConnectionString 속성에서 암호가 다시 읽히면 로그·예외 메시지로 샐 수 있다.
            var connectionString = SqlConnectionFactory.BuildSql("srv", "db", "sa", "p@ss");

            Assert.That(connectionString, Does.Not.Contain("Persist Security Info=True"));
        }
    }
}

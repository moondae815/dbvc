using System;
using System.IO;
using DBVC.Core;
using DBVC.Core.Models;
using NUnit.Framework;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class SqlConnectionFactoryTests
    {
        private string _path = null!;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(
                Path.GetTempPath(),
                "dbvc_cred_" + Guid.NewGuid().ToString("N"),
                "credentials.json");
        }

        [TearDown]
        public void TearDown()
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir != null && Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private SqlCredentialStore NewStore() => new SqlCredentialStore(_path, new ReversibleProtector());

        [Test]
        public void Build_UsesWindowsAuth_WhenNoCredentialIsStored()
        {
            // SQL 인증 도입 전에 매핑해 둔 데이터베이스가 그대로 동작해야 한다.
            var connectionString = new SqlConnectionFactory(NewStore()).Build("srv", "db");

            Assert.That(connectionString, Does.Contain("Integrated Security=True"));
            Assert.That(connectionString, Does.Contain("srv"));
            Assert.That(connectionString, Does.Contain("db"));
        }

        [Test]
        public void Build_UsesWindowsAuth_WhenTheStoredModeIsWindows()
        {
            var store = NewStore();
            store.Save("srv", "db", SqlAuthMode.Windows, null, null);

            var connectionString = new SqlConnectionFactory(store).Build("srv", "db");

            Assert.That(connectionString, Does.Contain("Integrated Security=True"));
        }

        [Test]
        public void Build_UsesTheStoredUserAndPassword_ForSqlAuth()
        {
            var store = NewStore();
            store.Save("srv", "db", SqlAuthMode.Sql, "sa", "p@ss");

            var connectionString = new SqlConnectionFactory(store).Build("srv", "db");

            Assert.That(connectionString, Does.Contain("User ID=sa"));
            Assert.That(connectionString, Does.Contain("p@ss"));
            Assert.That(connectionString, Does.Not.Contain("Integrated Security=True"));
        }

        [Test]
        public void Build_Throws_WhenSqlAuthHasNoUsablePassword()
        {
            var store = NewStore();
            store.Save("srv", "db", SqlAuthMode.Sql, "sa", "");

            var factory = new SqlConnectionFactory(store);

            var ex = Assert.Throws<SqlCredentialException>(() => factory.Build("srv", "db"));
            Assert.That(ex!.Message, Does.Contain("SQL 인증"),
                "영문 libgit2 스타일 원문 대신 한국어 안내가 나와야 합니다");
        }

        [Test]
        public void Build_Throws_WhenSqlAuthHasNoUserName()
        {
            var store = NewStore();
            store.Save("srv", "db", SqlAuthMode.Sql, null, "p@ss");

            var factory = new SqlConnectionFactory(store);

            Assert.Throws<SqlCredentialException>(() => factory.Build("srv", "db"));
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

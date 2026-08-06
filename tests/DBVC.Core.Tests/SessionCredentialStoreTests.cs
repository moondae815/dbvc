using System;
using System.IO;
using DBVC.Core;
using DBVC.Core.Models;
using NUnit.Framework;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class SessionCredentialStoreTests
    {
        [Test]
        public void TryGet_ReturnsNull_ForAnUnknownTarget()
        {
            Assert.That(new SessionCredentialStore().TryGet("srv", "db"), Is.Null);
        }

        [Test]
        public void Set_ThenTryGet_RoundTripsAllFourValues()
        {
            var store = new SessionCredentialStore();

            store.Set("srv", "db", SqlAuthMode.Sql, "sa", "p@ss");

            var credential = store.TryGet("srv", "db");
            Assert.That(credential, Is.Not.Null);
            Assert.That(credential!.ServerName, Is.EqualTo("srv"));
            Assert.That(credential.DatabaseName, Is.EqualTo("db"));
            Assert.That(credential.AuthMode, Is.EqualTo(SqlAuthMode.Sql));
            Assert.That(credential.UserName, Is.EqualTo("sa"));
            Assert.That(credential.Password, Is.EqualTo("p@ss"));
        }

        [Test]
        public void TryGet_IgnoresCase_InTheServerAndDatabaseNames()
        {
            var store = new SessionCredentialStore();
            store.Set("SRV", "DB", SqlAuthMode.Sql, "sa", "p@ss");

            Assert.That(store.TryGet("srv", "db"), Is.Not.Null);
        }

        [Test]
        public void Set_OverwritesEveryValue_LeavingNothingFromThePreviousCall()
        {
            // Save(plainPassword: null)이 "저장된 암호를 그대로 둔다"였던 옛 계약을 물려받으면 안 된다.
            // 대상이 같아도 개체 탐색기가 준 값이 통째로 이긴다.
            var store = new SessionCredentialStore();
            store.Set("srv", "db", SqlAuthMode.Sql, "sa", "old");

            store.Set("srv", "db", SqlAuthMode.Sql, "sa", null);

            Assert.That(store.TryGet("srv", "db")!.Password, Is.Null,
                "이전 호출의 암호가 남으면 사라진 계정의 암호로 접속을 시도하게 됩니다");
        }

        [Test]
        public void Set_DropsTheUserAndPassword_ForWindowsAuth()
        {
            var store = new SessionCredentialStore();
            store.Set("srv", "db", SqlAuthMode.Sql, "sa", "p@ss");

            store.Set("srv", "db", SqlAuthMode.Windows, "sa", "p@ss");

            var credential = store.TryGet("srv", "db")!;
            Assert.That(credential.UserName, Is.Null);
            Assert.That(credential.Password, Is.Null,
                "Windows 인증에는 암호가 필요 없고, 들고 있으면 언젠가 잘못된 대상에 쓰입니다");
        }

        [Test]
        public void Set_Throws_WhenTheServerOrDatabaseIsBlank()
        {
            var store = new SessionCredentialStore();

            Assert.Throws<ArgumentException>(() => store.Set(" ", "db", SqlAuthMode.Windows, null, null));
            Assert.Throws<ArgumentException>(() => store.Set("srv", " ", SqlAuthMode.Windows, null, null));
        }

        [Test]
        public void Set_WritesNothingToDisk()
        {
            // 이번 결정의 핵심 계약이다. 예전 계약("파일 내용에 암호가 없다")보다 강하다 —
            // 파일 자체가 생기지 않아야 한다.
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DBVC");
            var credentialFile = Path.Combine(appData, "credentials.json");
            var existedBefore = File.Exists(credentialFile);

            new SessionCredentialStore().Set("srv", "db", SqlAuthMode.Sql, "sa", "p@ss");

            Assert.That(File.Exists(credentialFile), Is.EqualTo(existedBefore),
                "메모리 전용 저장소가 %APPDATA%에 파일을 만들거나 지워서는 안 됩니다");
        }
    }
}

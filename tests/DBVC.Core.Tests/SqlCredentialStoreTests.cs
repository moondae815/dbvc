using System;
using System.IO;
using DBVC.Core;
using DBVC.Core.Models;
using NUnit.Framework;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 암호 보호를 흉내내는 테스트용 구현.
    /// 되돌릴 수 있으면서 원문과 다른 형태여야 "평문이 파일에 남지 않는다"를 검증할 수 있다.
    /// </summary>
    internal class ReversibleProtector : IPasswordProtector
    {
        private readonly bool _supported;

        public ReversibleProtector(bool supported = true)
        {
            _supported = supported;
        }

        public bool IsSupported => _supported;

        public string? Protect(string? plainText, string purpose)
        {
            if (!_supported || plainText == null) return null;
            return purpose + "|" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plainText));
        }

        public string? Unprotect(string? protectedText, string purpose)
        {
            if (!_supported || string.IsNullOrEmpty(protectedText)) return null;

            var prefix = purpose + "|";
            // 엔트로피가 다르면(=다른 항목의 값이면) 풀리지 않아야 한다.
            if (!protectedText!.StartsWith(prefix, StringComparison.Ordinal)) return null;

            return System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(protectedText.Substring(prefix.Length)));
        }
    }

    [TestFixture]
    public class SqlCredentialStoreTests
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

        private SqlCredentialStore NewStore(IPasswordProtector? protector = null)
            => new SqlCredentialStore(_path, protector ?? new ReversibleProtector());

        // ---------- 기본 동작 ----------

        [Test]
        public void TryGet_ReturnsNull_WhenNothingWasSaved()
        {
            Assert.That(NewStore().TryGet("srv", "db"), Is.Null);
        }

        [Test]
        public void Save_ThenTryGet_RoundTripsTheAuthModeAndUserName()
        {
            var store = NewStore();

            store.Save("srv", "db", SqlAuthMode.Sql, "sa", "p@ss");
            var credential = store.TryGet("srv", "db");

            Assert.That(credential, Is.Not.Null);
            Assert.That(credential!.AuthMode, Is.EqualTo(SqlAuthMode.Sql));
            Assert.That(credential.UserName, Is.EqualTo("sa"));
            Assert.That(store.ResolvePassword(credential), Is.EqualTo("p@ss"));
        }

        [Test]
        public void Save_PersistsAcrossInstances()
        {
            NewStore().Save("srv", "db", SqlAuthMode.Sql, "sa", "p@ss");

            var reloaded = NewStore().TryGet("srv", "db");

            Assert.That(reloaded, Is.Not.Null);
            Assert.That(reloaded!.UserName, Is.EqualTo("sa"));
            Assert.That(NewStore().ResolvePassword(reloaded), Is.EqualTo("p@ss"));
        }

        // ---------- 평문이 디스크에 닿지 않는다 ----------

        [Test]
        public void Save_NeverWritesThePasswordInPlainText()
        {
            NewStore().Save("srv", "db", SqlAuthMode.Sql, "sa", "SuperSecret123");

            var onDisk = File.ReadAllText(_path);

            Assert.That(onDisk, Does.Not.Contain("SuperSecret123"),
                "암호가 평문으로 파일에 남으면 DPAPI를 쓰는 의미가 없습니다");
            Assert.That(onDisk, Does.Contain("sa"), "사용자명은 평문이어도 됩니다");
        }

        [Test]
        public void Save_WritesTheAuthModeAsAReadableName()
        {
            NewStore().Save("srv", "db", SqlAuthMode.Sql, "sa", "p@ss");

            Assert.That(File.ReadAllText(_path), Does.Contain("Sql"),
                "사용자가 파일을 열었을 때 0/1이 아니라 이름이 보여야 합니다");
        }

        // ---------- 암호 유지·삭제 규칙 ----------

        [Test]
        public void Save_KeepsTheExistingPassword_WhenPasswordIsNull()
        {
            var store = NewStore();
            store.Save("srv", "db", SqlAuthMode.Sql, "sa", "p@ss");

            // 사용자가 암호 칸을 비운 채 Connect를 다시 누른 상황.
            store.Save("srv", "db", SqlAuthMode.Sql, "sa", null);

            Assert.That(store.ResolvePassword(store.TryGet("srv", "db")), Is.EqualTo("p@ss"),
                "암호를 입력하지 않았다고 저장된 암호를 지우면 매번 다시 입력해야 합니다");
        }

        [Test]
        public void Save_ClearsThePassword_WhenPasswordIsEmpty()
        {
            var store = NewStore();
            store.Save("srv", "db", SqlAuthMode.Sql, "sa", "p@ss");

            store.Save("srv", "db", SqlAuthMode.Sql, "sa", "");

            Assert.That(store.ResolvePassword(store.TryGet("srv", "db")), Is.Null);
        }

        [Test]
        public void Save_DropsTheStoredPassword_WhenSwitchingBackToWindowsAuth()
        {
            var store = NewStore();
            store.Save("srv", "db", SqlAuthMode.Sql, "sa", "p@ss");

            store.Save("srv", "db", SqlAuthMode.Windows, null, null);

            var credential = store.TryGet("srv", "db");
            Assert.That(credential!.AuthMode, Is.EqualTo(SqlAuthMode.Windows));
            Assert.That(credential.ProtectedPassword, Is.Null,
                "Windows 인증으로 되돌렸으면 암호를 들고 있을 이유가 없습니다");
            Assert.That(credential.UserName, Is.Null);
        }

        // ---------- 보호 실패 ----------

        [Test]
        public void Save_ReturnsFalse_WhenThePasswordCannotBeProtected()
        {
            var store = NewStore(new ReversibleProtector(supported: false));

            bool fullySaved = store.Save("srv", "db", SqlAuthMode.Sql, "sa", "p@ss");

            Assert.That(fullySaved, Is.False,
                "암호를 보호하지 못했다면 호출자가 알아야 합니다");
            Assert.That(store.CanPersistPasswords, Is.False);
        }

        [Test]
        public void ResolvePassword_ReturnsNull_WhenTheValueBelongsToAnotherEntry()
        {
            var store = NewStore();
            store.Save("srv", "db", SqlAuthMode.Sql, "sa", "p@ss");
            var stolen = store.TryGet("srv", "db")!.ProtectedPassword;

            // 다른 (서버, DB) 항목에 그대로 붙여넣은 상황.
            store.Save("other", "db2", SqlAuthMode.Sql, "sa", null);
            var forged = store.TryGet("other", "db2")!;
            forged.ProtectedPassword = stolen;

            Assert.That(store.ResolvePassword(forged), Is.Null,
                "항목별 엔트로피가 걸려 있어 다른 항목으로 옮긴 값은 풀리면 안 됩니다");
        }

        // ---------- 제거 ----------

        [Test]
        public void Remove_DeletesTheEntry()
        {
            var store = NewStore();
            store.Save("srv", "db", SqlAuthMode.Sql, "sa", "p@ss");

            Assert.That(store.Remove("srv", "db"), Is.True);
            Assert.That(store.TryGet("srv", "db"), Is.Null);
            Assert.That(store.Remove("srv", "db"), Is.False);
        }

        // ---------- 손상된 파일 ----------

        [Test]
        public void Load_StartsEmpty_WhenTheFileIsCorrupt()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, "{ this is not valid json");

            SqlCredentialStore store = null!;
            Assert.DoesNotThrow(() => store = NewStore(),
                "손상된 파일 하나로 플러그인이 죽어서는 안 됩니다");
            Assert.That(store.TryGet("srv", "db"), Is.Null);
        }

        [Test]
        public void Save_ThrowsArgumentException_WhenServerOrDatabaseIsMissing()
        {
            var store = NewStore();

            Assert.Throws<ArgumentException>(() => store.Save("", "db", SqlAuthMode.Windows, null, null));
            Assert.Throws<ArgumentException>(() => store.Save("srv", "", SqlAuthMode.Windows, null, null));
        }
    }
}

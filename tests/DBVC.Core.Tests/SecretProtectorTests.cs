using System;
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class SecretProtectorTests
    {
        [Test]
        public void Unprotect_ReturnsOriginal_WhenRoundTripped()
        {
            var protector = new DpapiSecretProtector();

            var protectedText = protector.Protect("sk-test-1234");

            Assert.That(protector.Unprotect(protectedText), Is.EqualTo("sk-test-1234"));
        }

        [Test]
        public void Protect_DoesNotContainPlainText_WhenProtected()
        {
            var protector = new DpapiSecretProtector();

            var protectedText = protector.Protect("sk-test-1234");

            // 이 단언이 이 클래스의 존재 이유다. 통과하지 않으면 뒤의 모든 저장 로직이 무의미하다.
            Assert.That(protectedText, Does.Not.Contain("sk-test-1234"));
        }

        [Test]
        public void Unprotect_ReturnsNull_WhenTextIsNotProtectedData()
        {
            var protector = new DpapiSecretProtector();

            // 사용자가 파일을 손으로 고쳤거나 다른 계정이 암호화한 값이다.
            // 예외가 아니라 null이어야 화면이 "키를 다시 입력하세요"로 흘러간다.
            Assert.That(protector.Unprotect("not-protected-data"), Is.Null);
        }
    }
}

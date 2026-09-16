using System;
using System.Security.Cryptography;
using System.Text;

namespace DBVC.Core
{
    /// <summary>
    /// 디스크에 남겨야 하는 비밀을 보호한다.
    ///
    /// 이음매로 둔 이유는 테스트다 — 이것이 없으면 설정 저장소를 검증할 때마다
    /// 실행 계정의 진짜 DPAPI 키를 타게 되고, 실패했을 때 저장 로직이 틀린 것인지
    /// 암호화가 틀린 것인지 구분되지 않는다.
    /// </summary>
    public interface ISecretProtector
    {
        string Protect(string plainText);

        /// <summary>복원할 수 없으면 <c>null</c>이다. 다른 계정이 암호화했거나 파일이 손상된 경우다.</summary>
        string? Unprotect(string protectedText);
    }

    /// <summary>
    /// Windows DPAPI(CurrentUser)로 보호한다. 같은 PC의 다른 계정은 복호화하지 못한다.
    /// </summary>
    public sealed class DpapiSecretProtector : ISecretProtector
    {
        public string Protect(string plainText)
        {
            if (plainText == null) throw new ArgumentNullException(nameof(plainText));

            var bytes = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        public string? Unprotect(string protectedText)
        {
            if (string.IsNullOrWhiteSpace(protectedText)) return null;

            try
            {
                var protectedBytes = Convert.FromBase64String(protectedText);
                var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException)
            {
                // Base64가 아니다 — 손으로 고친 파일이다.
                return null;
            }
            catch (CryptographicException)
            {
                // 다른 계정이 암호화했거나 값이 깨졌다.
                return null;
            }
        }
    }
}

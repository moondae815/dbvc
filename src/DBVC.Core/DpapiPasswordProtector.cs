using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DBVC.Core
{
    /// <summary>
    /// Windows DPAPI(<see cref="DataProtectionScope.CurrentUser"/>)로 암호를 보호한다.
    /// 복호화는 보호를 수행한 Windows 사용자 계정에서만 가능하므로,
    /// credentials.json이 그대로 유출되어도 다른 계정에서는 평문을 얻을 수 없다.
    ///
    /// 비Windows에서는 <see cref="IsSupported"/>가 false이고 모든 연산이 <c>null</c>을 반환한다.
    /// 예외를 던지지 않는 이유는 크로스플랫폼 테스트에서 이 타입을 그대로 생성할 수 있게 하기 위해서다.
    /// </summary>
    public class DpapiPasswordProtector : IPasswordProtector
    {
        /// <summary>
        /// 플랫폼 지원 여부는 프로세스 수명 동안 바뀌지 않으므로 한 번만 확인한다.
        /// </summary>
        private static readonly Lazy<bool> SupportedOnThisPlatform = new Lazy<bool>(DetectSupport);

        private bool _isSupported => SupportedOnThisPlatform.Value;

        public bool IsSupported => _isSupported;

        public string? Protect(string? plainText, string purpose)
        {
            if (!_isSupported || plainText == null) return null;

            try
            {
                var protectedBytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plainText),
                    GetEntropy(purpose),
                    DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(protectedBytes);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DpapiPasswordProtector.Protect failed: {ex.Message}");
                return null;
            }
        }

        public string? Unprotect(string? protectedText, string purpose)
        {
            if (!_isSupported || string.IsNullOrEmpty(protectedText)) return null;

            try
            {
                var plainBytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(protectedText!),
                    GetEntropy(purpose),
                    DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (Exception ex)
            {
                // 다른 계정에서 만든 값이거나 파일이 손상된 경우다.
                // 호출자는 null을 "암호를 다시 입력받아야 한다"로 해석한다.
                Debug.WriteLine($"DpapiPasswordProtector.Unprotect failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 항목별 엔트로피. 보호된 값을 다른 (서버, DB) 항목에 붙여넣어도 복호화되지 않게 한다.
        /// </summary>
        private static byte[] GetEntropy(string purpose)
        {
            return Encoding.UTF8.GetBytes("DBVC.SqlCredential:" + (purpose ?? string.Empty));
        }

        private static bool DetectSupport()
        {
            try
            {
                // ProtectedData는 비Windows에서 PlatformNotSupportedException을 던진다.
                // 플랫폼 문자열로 짐작하는 대신 실제로 한 번 호출해 확인한다.
                ProtectedData.Protect(new byte[] { 0 }, null, DataProtectionScope.CurrentUser);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}

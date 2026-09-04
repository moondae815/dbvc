using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// 로그온 계정에서 커밋 작성자 초깃값을 추정한다.
    ///
    /// 얇은 어댑터다. 판단(형식이 쓸 만한가)은 하지 않는다 - 그것은 Core의
    /// <c>GitIdentity.Validate</c>가 한다. SSMS 어댑터에서 판단 로직을 SsmsUrn으로 뺀 것과
    /// 같은 규칙이다.
    ///
    /// 추정값을 자동 확정하지 않는 이유는 메일이 틀렸을 때 드러나는 시점이 첫 MR이기
    /// 때문이다. 사람이 한 번 보게 한다.
    /// </summary>
    public static class WindowsAccountIdentity
    {
        // EXTENDED_NAME_FORMAT. 3 = NameDisplay(표시 이름), 8 = NameUserPrincipal(UPN).
        private const int NameDisplay = 3;
        private const int NameUserPrincipal = 8;

        // 반환형이 네이티브로는 1바이트 BOOLEAN이다. 여기를 C# bool(4바이트 UnmanagedType.Bool)로
        // 두면 콜리가 하위 1바이트만 채워도 상위 3바이트의 쓰레기값을 성공으로 읽을 수 있다 -
        // 도메인 미가입 PC에서 ERROR_NONE_MAPPED로 실패하는 바로 그 경로가 성공으로 둔갑한다.
        [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool GetUserNameEx(int nameFormat, StringBuilder nameBuffer, ref uint size);

        /// <summary>
        /// 추정한 (이름, 메일). 도메인에 가입되지 않은 PC에서는 두 형식 모두 실패하므로
        /// 이름은 Windows 계정명으로 대체하고 메일은 빈 문자열로 둔다.
        /// </summary>
        public static (string Name, string Email) Suggest()
        {
            var name = Query(NameDisplay);
            var email = Query(NameUserPrincipal);

            if (string.IsNullOrWhiteSpace(name))
            {
                name = Environment.UserName ?? string.Empty;
            }

            return (name ?? string.Empty, email ?? string.Empty);
        }

        private static string? Query(int format)
        {
            try
            {
                uint size = 256;
                var buffer = new StringBuilder((int)size);

                if (GetUserNameEx(format, buffer, ref size)) return buffer.ToString();

                // 버퍼가 작으면 필요한 크기를 size에 돌려준다. 한 번만 다시 시도한다.
                buffer = new StringBuilder((int)size);
                return GetUserNameEx(format, buffer, ref size) ? buffer.ToString() : null;
            }
            catch (Exception ex)
            {
                // 추정 실패는 결함이 아니다. 사용자가 두 칸을 직접 치면 된다.
                Debug.WriteLine($"WindowsAccountIdentity.Query({format}) failed: {ex.Message}");
                return null;
            }
        }
    }
}

using DBVC.Core.Models;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// SSMS가 들고 있는 연결에서 DBVC가 쓸 수 있는 형태로 옮겨 담은 값.
    ///
    /// <see cref="ServerName"/>과 <see cref="DatabaseName"/>이 non-null인 것은 계약이다.
    /// 둘 중 하나라도 확정할 수 없으면 <see cref="ISsmsConnectionSource.TryGetCurrent"/>가
    /// <c>null</c>을 반환한다 — 절반짜리 값으로 입력란을 채우면 사용자가 직접 입력해 둔
    /// 데이터베이스 이름을 지우게 된다.
    /// </summary>
    public sealed class SsmsConnectionInfo
    {
        public SsmsConnectionInfo(
            string serverName,
            string databaseName,
            SqlAuthMode authMode,
            string? userName,
            string? password,
            string? unsupportedReason)
        {
            ServerName = serverName;
            DatabaseName = databaseName;
            AuthMode = authMode;
            UserName = userName;
            Password = password;
            UnsupportedReason = unsupportedReason;
        }

        public string ServerName { get; }
        public string DatabaseName { get; }

        /// <summary>
        /// <see cref="UnsupportedReason"/>이 non-null이면 이 값은 의미가 없다 — 어댑터의 두 미지원
        /// 분기(Entra ID, 계정명 없음)가 서로 다른 값(Windows/Sql)을 채워 넣지만, 호출자는
        /// <see cref="UnsupportedReason"/>이 있으면 이 필드를 읽지 않는다.
        /// </summary>
        public SqlAuthMode AuthMode { get; }
        public string? UserName { get; }

        /// <summary>SSMS가 암호를 들고 있지 않으면 <c>null</c>. 그 경우 저장된 암호로 폴백한다.</summary>
        public string? Password { get; }

        /// <summary>
        /// 이 연결을 그대로 재사용할 수 없는 사유(Entra ID 등). <c>null</c>이면 재사용 가능하다.
        /// 사유가 있어도 서버·데이터베이스는 쓸 수 있으므로 값 자체는 채워서 보낸다.
        /// </summary>
        public string? UnsupportedReason { get; }
    }

    /// <summary>
    /// SSMS 셸에서 현재 연결을 읽는 경로. ViewModel은 이 인터페이스만 안다 —
    /// 구현이 리플렉션이라 SSMS 프로세스 밖에서는 테스트할 수 없기 때문이다.
    /// </summary>
    public interface ISsmsConnectionSource
    {
        /// <summary>현재 선택에서 연결을 읽는다. 읽을 수 없으면 <c>null</c>(예외를 던지지 않는다).</summary>
        SsmsConnectionInfo? TryGetCurrent();
    }
}

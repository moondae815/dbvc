namespace DBVC.Core.Models
{
    /// <summary>
    /// SQL Server 접속에 사용할 인증 방식.
    /// </summary>
    public enum SqlAuthMode
    {
        /// <summary>Windows 통합 인증. 사용자명·암호가 필요 없다.</summary>
        Windows = 0,

        /// <summary>SQL Server 인증. 사용자명과 암호가 필요하다.</summary>
        Sql = 1
    }

    /// <summary>
    /// 한 (서버, 데이터베이스)에 접속할 때 쓸 인증 정보.
    ///
    /// <see cref="Password"/>는 평문이며 이 프로세스 안에서만 산다 — 디스크에 닿는 경로가 없다.
    /// 이 타입을 로그나 예외 메시지에 통째로 싣지 말 것. <c>ToString()</c>을 재정의하지 않는 것도
    /// 같은 이유다.
    /// </summary>
    public class SqlCredential
    {
        public string ServerName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public SqlAuthMode AuthMode { get; set; } = SqlAuthMode.Windows;

        /// <summary><see cref="SqlAuthMode.Sql"/>일 때만 의미가 있다.</summary>
        public string? UserName { get; set; }

        /// <summary>
        /// 평문 암호. 이 프로세스가 사는 동안만 존재하며 디스크에 닿지 않는다.
        ///
        /// 값의 출처는 SSMS 개체 탐색기뿐이고, SSMS가 닫히면 함께 사라진다.
        /// 이 타입을 로그에 통째로 싣지 말 것 — 진단에는 존재 여부만 남긴다.
        /// </summary>
        public string? Password { get; set; }
    }
}

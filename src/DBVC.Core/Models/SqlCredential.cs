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
    /// 암호는 <see cref="ProtectedPassword"/>(보호된 형태)로만 보관하며 평문은 이 타입에 담지 않는다.
    /// 평문이 필요한 시점은 연결 문자열을 만들 때뿐이고, 그때만
    /// <see cref="DBVC.Core.ISqlCredentialStore.ResolvePassword"/>로 되돌린다.
    /// </summary>
    public class SqlCredential
    {
        public string ServerName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public SqlAuthMode AuthMode { get; set; } = SqlAuthMode.Windows;

        /// <summary><see cref="SqlAuthMode.Sql"/>일 때만 의미가 있다.</summary>
        public string? UserName { get; set; }

        /// <summary>
        /// <see cref="IPasswordProtector"/>가 보호한 암호. 구현에 따라 형식이 다르므로
        /// 이 문자열을 직접 해석하지 않는다.
        /// </summary>
        public string? ProtectedPassword { get; set; }
    }
}

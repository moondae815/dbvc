namespace DBVC.Core.Models
{
    /// <summary>커밋 대상 객체 하나를 만진 다른 작업자 한 명.</summary>
    public class CoAuthorWarning
    {
        public string QualifiedName { get; set; } = string.Empty;

        /// <summary>접속 PC 이름. 알 수 없으면 로그인 이름이 대신 온다.</summary>
        public string Author { get; set; } = string.Empty;
    }
}

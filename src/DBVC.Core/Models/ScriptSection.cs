namespace DBVC.Core.Models
{
    /// <summary>생성할 스크립트의 종류.</summary>
    public enum ScriptKind
    {
        /// <summary>현재 DB 기준 최신 코드를 병합한다. (Feature 8)</summary>
        Deployment,

        /// <summary>마지막 커밋 직전 상태의 코드를 병합한다. (Feature 9)</summary>
        Rollback
    }

    /// <summary>
    /// 병합 스크립트에 들어갈 객체 하나의 DDL 조각.
    /// </summary>
    public class ScriptSection
    {
        public string QualifiedName { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string? Sql { get; set; }
    }
}

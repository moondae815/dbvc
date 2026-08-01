using System;
using System.Collections.Generic;

namespace DBVC.Core.Models
{
    /// <summary>
    /// 스크립팅 대상 객체 하나를 식별한다. SMO 타입에 의존하지 않아 DB 없이 테스트할 수 있다.
    /// </summary>
    public class ScriptTargetInfo
    {
        public string? Schema { get; set; }
        public string Name { get; set; } = string.Empty;
        public string ObjectType { get; set; } = string.Empty;

        /// <summary>SMO Urn 등 어댑터가 필요로 하는 원본 핸들.</summary>
        public object? Tag { get; set; }

        public string QualifiedName => ObjectPathConvention.GetQualifiedName(Schema, Name);
        public string RelativePath => ObjectPathConvention.GetRelativePath(Schema, ObjectType, Name);
    }

    /// <summary>
    /// 스크립팅 결과. 설계 3.1에 따라 일부 객체가 실패해도 전체는 계속 진행되며,
    /// 실패한 객체는 여기에 모아 보고한다.
    /// </summary>
    public class ScriptResult
    {
        public int SucceededCount { get; set; }
        public List<string> FailedObjects { get; } = new List<string>();
        public bool HasFailures => FailedObjects.Count > 0;
    }
}

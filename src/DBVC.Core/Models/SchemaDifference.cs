using System.Collections.Generic;

namespace DBVC.Core.Models
{
    /// <summary>
    /// 대상 DB와 브랜치가 어긋난 방식. "같음"은 값이 없다 — 결과에 담지 않기 때문이다.
    /// 수천 개가 되고 화면에 쓸 데가 없으며, 개수는 <see cref="ComparisonResult.ComparedCount"/>가 말한다.
    /// </summary>
    public enum ObjectDiffState
    {
        /// <summary>양쪽에 있고 바이트가 다르다.</summary>
        Modified,

        /// <summary>브랜치에만 있다. 배포되지 않았다.</summary>
        MissingInDatabase,

        /// <summary>DB에만 있다. 커밋되지 않았거나 무단 추가다.</summary>
        MissingInBranch
    }

    /// <summary>객체 하나의 차이. 화면과 배포 스크립트 분류가 같은 것을 본다.</summary>
    public class SchemaDifference
    {
        public SchemaDifference(string qualifiedName, string relativePath, string objectType, ObjectDiffState state)
        {
            QualifiedName = qualifiedName;
            RelativePath = relativePath;
            ObjectType = objectType;
            State = state;
        }

        public string QualifiedName { get; }
        public string RelativePath { get; }

        /// <summary>SMO 타입명(<c>Table</c>, <c>StoredProcedure</c> 등). 분류의 축이다.</summary>
        public string ObjectType { get; }

        public ObjectDiffState State { get; }
    }

    /// <summary>
    /// 차이 검사 한 번의 결과. 저장소에는 아무것도 쓰지 않았으므로 되돌릴 것이 없다.
    /// </summary>
    public class ComparisonResult
    {
        public List<SchemaDifference> Differences { get; } = new List<SchemaDifference>();

        /// <summary>스크립팅에 실패해 판정하지 못한 객체. 차이가 아니라 "모른다"이다.</summary>
        public List<string> FailedObjects { get; } = new List<string>();

        /// <summary>대상 DB에서 훑은 객체 수. "n개 중 m개 차이"의 분모다.</summary>
        public int ComparedCount { get; set; }

        public bool IsInSync => Differences.Count == 0;
    }
}

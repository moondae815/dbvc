namespace DBVC.Core.Models
{
    /// <summary>
    /// 매핑 대상에 허용되는 동작의 범위. 값의 순서는 제한이 강해지는 순서다 —
    /// 모르는 값을 만났을 때 가장 제한적인 쪽으로 떨어뜨리는 근거가 된다.
    ///
    /// 1차에서는 저장·직렬화만 하고 동작을 막지는 않는다. 지금 필드를 넣는 이유는
    /// 사용자가 만든 mappings.json을 나중에 마이그레이션하지 않기 위해서다.
    /// </summary>
    public enum MappingMode
    {
        /// <summary>개발 DB. 추출·커밋·Push·트리거 설치가 모두 허용된다.</summary>
        Write = 0,

        /// <summary>테스트 DB. 차이 검사와 배포 스크립트 생성만 한다.</summary>
        Deploy = 1,

        /// <summary>운영 DB. 차이 검사만 한다.</summary>
        Audit = 2
    }
}

namespace DBVC.Core.Models
{
    public class MappingConfig
    {
        public string ServerName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public string GitPath { get; set; } = string.Empty;

        /// <summary>
        /// 이 저장소가 고정되어야 할 브랜치. 비면 전환이 자유롭다(개발 클론).
        ///
        /// 감사·배포용 클론에서 이 값이 어긋난 채로 비교하면 화면이 조용히 거짓말을 한다 —
        /// 운영 폴더가 develop을 가리키면 개발과 운영의 모든 차이가 "무단 변경"으로 보고된다.
        /// 그래서 판정 결과는 경고가 아니라 차단이다(RepositoryStateEvaluator).
        /// </summary>
        public string? Branch { get; set; }

        /// <summary>허용 동작의 범위. 값이 없는 구버전 파일은 <see cref="MappingMode.Write"/>로 읽힌다.</summary>
        public MappingMode Mode { get; set; } = MappingMode.Write;
    }
}

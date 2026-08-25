namespace DBVC.Core.Models
{
    /// <summary>
    /// <c>DBVC_ChangeLog</c>에서 읽은 원시 DDL 이벤트 한 건.
    /// </summary>
    public class ChangeLogRow
    {
        public long Id { get; set; }
        public string? SchemaName { get; set; }
        public string ObjectName { get; set; } = string.Empty;
        public string ObjectType { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;

        /// <summary>인덱스·컬럼처럼 다른 객체에 딸린 이벤트의 부모. 없으면 null이다.</summary>
        public string? TargetObjectName { get; set; }

        /// <summary>
        /// 부모의 타입(<c>TABLE</c>·<c>VIEW</c> 등). 인덱싱된 뷰의 인덱스는 여기가 <c>VIEW</c>로 오므로
        /// 정규화가 이 값을 그대로 옮긴다 — <c>TABLE</c>로 못박으면 뷰가 Tables 폴더로 떨어진다.
        /// </summary>
        public string? TargetObjectType { get; set; }

        /// <summary>DDL을 실행한 SQL 로그인. 공용 계정 환경에서는 모든 행에서 같다.</summary>
        public string? LoginName { get; set; }

        /// <summary>
        /// DDL을 실행한 접속의 워크스테이션 이름(<c>HOST_NAME()</c>).
        /// 공용 계정을 쓰는 환경에서 사람을 가르는 유일한 축이다. v3 이전 행은 null이다.
        /// </summary>
        public string? HostName { get; set; }
    }

    /// <summary>
    /// DDL 로그와 Git 작업 트리 상태를 종합한 객체 하나의 최종 변경 상태. (설계 3.3)
    /// </summary>
    public class ChangeRecord
    {
        public string? Schema { get; set; }
        public string ObjectName { get; set; } = string.Empty;
        public string ObjectType { get; set; } = string.Empty;

        /// <summary>Modified / Added / Deleted</summary>
        public string State { get; set; } = string.Empty;

        /// <summary><c>dbo.Users</c> 형태의 스키마 한정 이름.</summary>
        public string QualifiedName { get; set; } = string.Empty;

        /// <summary><c>dbo/Tables/Users.sql</c> 형태의 저장소 상대 경로.</summary>
        public string RelativePath { get; set; } = string.Empty;

        /// <summary>
        /// 이 상태의 근거가 된 가장 최신 로그 행의 Id. 커밋 후 해당 행까지만 처리 완료로 표시한다.
        /// Git 상태에서만 유래한 항목은 0이다.
        /// </summary>
        public long LastLogId { get; set; }

        /// <summary>이 상태의 근거가 된 가장 최신 로그 행의 SQL 로그인.</summary>
        public string? Author { get; set; }

        /// <summary>
        /// 이 상태의 근거가 된 가장 최신 로그 행의 접속 PC.
        /// MarkProcessed가 현재 사용자가 아니라 이 값으로 좁힌다 - 전체 보기에서 남의 변경을
        /// 대신 커밋하는 경로가 있고, 현재 사용자로 좁히면 그 행이 영원히 닫히지 않는다.
        /// </summary>
        public string? HostName { get; set; }
    }
}

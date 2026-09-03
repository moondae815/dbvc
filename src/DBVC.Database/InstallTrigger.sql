-- DBVC (DB Version Control) DDL Trigger & ChangeLog Setup Script
-- Target: Microsoft SQL Server
-- 이 스크립트는 멱등(idempotent)하다. 이미 설치된 데이터베이스에 다시 실행해도 안전하며,
-- 구버전 스키마에는 누락된 컬럼만 추가한다.

-- 아무것도 지우거나 고치기 전에 dbo 가장 권한부터 확인한다.
-- 트리거를 지우는 DROP TRIGGER ... ON DATABASE는 ALTER ANY DATABASE DDL TRIGGER면 되지만
-- 새 트리거의 WITH EXECUTE AS 'dbo'는 dbo에 대한 IMPERSONATE를 요구한다. 확인 없이 진행하면
-- 기존 트리거를 지운 뒤 CREATE가 실패해 변경 추적이 통째로 꺼진 채 남는다 — 그 뒤의 모든
-- 스키마 변경이 로그 없이 지나가고, 화면은 미설치로 보여 다시 눌러도 같은 자리에서 실패한다.
BEGIN TRY
    EXECUTE AS USER = N'dbo';
    REVERT;
END TRY
BEGIN CATCH
    THROW 51000, N'DBVC 설치에는 dbo를 가장할 수 있는 권한(db_owner)이 필요합니다. 변경 추적기는 그대로 둡니다.', 1;
END CATCH
GO

-- 트리거는 이 두 옵션을 생성 시점 값으로 저장하고, 본문의 EVENTDATA().value()가 그것이 ON이어야
-- 동작한다. QUOTED_IDENTIFIER가 기본 OFF인 클라이언트(sqlcmd)로 설치하면 이 데이터베이스의
-- 모든 DDL이 오류 1934 -> 3616으로 실패한다. 클라이언트 기본값에 기대지 않는다.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[DBVC_ChangeLog] (
        [Id] INT IDENTITY(1,1) PRIMARY KEY,
        [EventType] NVARCHAR(100) NOT NULL,
        [SchemaName] NVARCHAR(128) NULL,
        [ObjectName] NVARCHAR(256) NOT NULL,
        [ObjectType] NVARCHAR(100) NOT NULL,
        [PostTime] DATETIME NOT NULL DEFAULT GETDATE(),
        [LoginName] NVARCHAR(256) NOT NULL,
        [TSQLCommand] NVARCHAR(MAX) NULL,
        [TargetObjectName] NVARCHAR(256) NULL,
        [TargetObjectType] NVARCHAR(100) NULL,
        [HostName] NVARCHAR(128) NULL,
        [ClientNetAddress] NVARCHAR(48) NULL,
        -- sp_rename은 옛 이름만 ObjectName에 남긴다. 새 이름을 여기 담지 않으면 바뀐 객체가
        -- 로그에 없어 추출되지 않고, 사라진 이름이 유령 항목으로 목록에 남는다.
        [NewObjectName] NVARCHAR(256) NULL,
        [IsProcessed] BIT NOT NULL DEFAULT 0
    );
END
GO

-- 구버전(SchemaName / IsProcessed 이전)에 설치된 테이블 보정
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND name = N'SchemaName')
BEGIN
    ALTER TABLE [dbo].[DBVC_ChangeLog] ADD [SchemaName] NVARCHAR(128) NULL;
END
GO

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND name = N'IsProcessed')
BEGIN
    ALTER TABLE [dbo].[DBVC_ChangeLog] ADD [IsProcessed] BIT NOT NULL CONSTRAINT [DF_DBVC_ChangeLog_IsProcessed] DEFAULT 0;
END
GO

-- v1(Target 컬럼 이전)에 설치된 테이블 보정. 인덱스 이벤트가 부모 테이블을 가리키는 유일한 근거다.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND name = N'TargetObjectName')
BEGIN
    ALTER TABLE [dbo].[DBVC_ChangeLog] ADD [TargetObjectName] NVARCHAR(256) NULL;
END
GO

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND name = N'TargetObjectType')
BEGIN
    ALTER TABLE [dbo].[DBVC_ChangeLog] ADD [TargetObjectType] NVARCHAR(100) NULL;
END
GO

-- v3 이전에 설치된 테이블에는 작업자 컬럼이 없다. NULL로 더한다 -
-- 기존 행은 작업자를 알 수 없으므로 "내 변경만"에서 빠지고 "전체"에서만 보인다.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND name = N'HostName')
BEGIN
    ALTER TABLE [dbo].[DBVC_ChangeLog] ADD [HostName] NVARCHAR(128) NULL;
END
GO

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND name = N'NewObjectName')
BEGIN
    ALTER TABLE [dbo].[DBVC_ChangeLog] ADD [NewObjectName] NVARCHAR(256) NULL;
END
GO

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND name = N'ClientNetAddress')
BEGIN
    ALTER TABLE [dbo].[DBVC_ChangeLog] ADD [ClientNetAddress] NVARCHAR(48) NULL;
END
GO

-- 미처리 변경 조회(RefreshState)의 주 조회 경로
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND name = N'IX_DBVC_ChangeLog_IsProcessed')
BEGIN
    CREATE NONCLUSTERED INDEX [IX_DBVC_ChangeLog_IsProcessed]
        ON [dbo].[DBVC_ChangeLog] ([IsProcessed], [PostTime] DESC);
END
GO

-- 로그를 읽고 닫는 일은 클라이언트가 접속 계정 그대로 한다 - 트리거의 INSERT만 dbo로 돈다.
-- 이 GRANT가 없으면 db_owner가 아닌 계정의 커밋이 로그를 닫지 못하고, 그 항목이 새로고침마다
-- 되살아난다. 커밋은 이미 성공한 뒤라 사용자에게는 원인이 보이지 않는다.
--
-- public에 주는 이유는 빠뜨릴 수 있는 등록 단계를 만들지 않기 위해서다. 역할을 만들어 두면
-- 사람을 넣는 것을 잊는 순간 똑같은 증상이 돌아온다. 노출되는 것은 그 DB의 DDL 이력인데,
-- 그 DB에 접속할 수 있는 사람은 sys.sql_modules로 어차피 같은 것을 읽는다.
--
-- INSERT는 주지 않는다. 트리거가 dbo로 쓰므로 필요 없고, 주면 사용자가 로그를 직접 조작할 수 있다.
GRANT SELECT, UPDATE ON [dbo].[DBVC_ChangeLog] TO [public];
GO

IF EXISTS (SELECT * FROM sys.triggers WHERE parent_class = 0 AND name = 'trg_DBVC_DDL_Tracker')
BEGIN
    DROP TRIGGER [trg_DBVC_DDL_Tracker] ON DATABASE;
END
GO

CREATE TRIGGER [trg_DBVC_DDL_Tracker]
ON DATABASE
-- 로깅 INSERT를 dbo 권한으로 돌린다. 사용자 권한으로 돌리면 ChangeLog에 쓸 수 없는 사용자의
-- DDL이 통째로 실패한다 - 트리거 안의 오류는 트랜잭션을 uncommittable로 만들어, CATCH로 삼켜도
-- SQL Server가 오류 3616으로 배치를 중단하고 롤백하기 때문이다. 그래서 CATCH도 두지 않는다:
-- 트리거 안의 오류를 무해하게 만드는 방법은 없고, 삼키는 척하는 코드는 잘못된 안심만 남긴다.
WITH EXECUTE AS 'dbo'
FOR DDL_DATABASE_LEVEL_EVENTS
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @EventData XML = EVENTDATA();

    DECLARE @ObjectName NVARCHAR(256) = @EventData.value('(/EVENT_INSTANCE/ObjectName)[1]', 'NVARCHAR(256)');

    -- DBVC 자체 테이블/트리거에 대한 DDL은 사용자 변경이 아니므로 기록하지 않는다.
    IF @ObjectName IS NULL OR @ObjectName IN (N'DBVC_ChangeLog', N'trg_DBVC_DDL_Tracker')
        RETURN;

    DECLARE @ObjectType NVARCHAR(100) = @EventData.value('(/EVENT_INSTANCE/ObjectType)[1]', 'NVARCHAR(100)');

    -- DBVC_TRACKED_TYPES: ObjectPathConvention.DdlEventObjectTypes + INDEX + COLUMN과 같아야 한다.
    -- InstallScriptSyncTests가 이 목록을 읽어 대조하므로 형식(따옴표 붙은 값 나열)을 바꾸지 말 것.
    -- 여기서 거르지 않으면 사용자·권한 이벤트가 파일 없는 항목으로 목록에 남는다.
    -- INDEX와 COLUMN은 독립 파일이 되지 않지만 기록한다 - Core가 부모 객체의 수정으로 정규화한다.
    -- 특히 컬럼 이름 변경(sp_rename)은 COLUMN 이벤트 하나만 남기고 테이블 이벤트를 내지 않아,
    -- 거르면 그 변경이 저장소에 영영 반영되지 않는다.
    IF @ObjectType IS NULL OR @ObjectType NOT IN (N'TABLE', N'VIEW', N'PROCEDURE', N'SQL_STORED_PROCEDURE',
        N'FUNCTION', N'SQL_SCALAR_FUNCTION', N'SQL_TABLE_VALUED_FUNCTION',
        N'SQL_INLINE_TABLE_VALUED_FUNCTION', N'TRIGGER', N'SQL_TRIGGER', N'TYPE',
        N'TABLE_TYPE', N'SEQUENCE OBJECT', N'SEQUENCE_OBJECT', N'SEQUENCE', N'SYNONYM',
        N'INDEX', N'COLUMN')
        RETURN;

    INSERT INTO [dbo].[DBVC_ChangeLog] (
        [EventType],
        [SchemaName],
        [ObjectName],
        [ObjectType],
        [PostTime],
        [LoginName],
        [TSQLCommand],
        [TargetObjectName],
        [TargetObjectType],
        [HostName],
        [ClientNetAddress],
        [NewObjectName],
        [IsProcessed]
    )
    VALUES (
        @EventData.value('(/EVENT_INSTANCE/EventType)[1]', 'NVARCHAR(100)'),
        @EventData.value('(/EVENT_INSTANCE/SchemaName)[1]', 'NVARCHAR(128)'),
        @ObjectName,
        @ObjectType,
        GETDATE(),
        @EventData.value('(/EVENT_INSTANCE/LoginName)[1]', 'NVARCHAR(256)'),
        @EventData.value('(/EVENT_INSTANCE/TSQLCommand/CommandText)[1]', 'NVARCHAR(MAX)'),
        @EventData.value('(/EVENT_INSTANCE/TargetObjectName)[1]', 'NVARCHAR(256)'),
        @EventData.value('(/EVENT_INSTANCE/TargetObjectType)[1]', 'NVARCHAR(100)'),
        -- 세션 범위 내장 함수라 WITH EXECUTE AS 'dbo'의 샌드박싱에 걸리지 않는다.
        -- sys.dm_exec_connections는 서버 범위라 이 문맥에서 읽을 수 없다.
        HOST_NAME(),
        -- CONNECTIONPROPERTY는 sql_variant를 돌려준다. CONVERT 없이 넣으면 형식이 안 맞는다.
        CONVERT(NVARCHAR(48), CONNECTIONPROPERTY('client_net_address')),
        -- RENAME 이벤트에만 채워진다. 다른 이벤트에서는 NULL이다.
        @EventData.value('(/EVENT_INSTANCE/NewObjectName)[1]', 'NVARCHAR(256)'),
        0
    );
END;
GO

-- 스키마 버전. Core(StateTracker.RequiredSchemaVersion)가 이 값을 보고 구버전 설치를 알아챈다.
-- 확장 속성을 쓰는 이유는 객체가 늘지 않고, 이 DDL 자체가 트리거의 DBVC_ChangeLog 예외에 걸려
-- 로그를 더럽히지 않기 때문이다.
IF NOT EXISTS (SELECT 1 FROM sys.extended_properties
               WHERE class = 1 AND major_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]')
                 AND minor_id = 0 AND name = N'DBVC_SchemaVersion')
BEGIN
    EXEC sp_addextendedproperty @name = N'DBVC_SchemaVersion', @value = N'5',
         @level0type = N'SCHEMA', @level0name = N'dbo',
         @level1type = N'TABLE',  @level1name = N'DBVC_ChangeLog';
END
ELSE
BEGIN
    EXEC sp_updateextendedproperty @name = N'DBVC_SchemaVersion', @value = N'5',
         @level0type = N'SCHEMA', @level0name = N'dbo',
         @level1type = N'TABLE',  @level1name = N'DBVC_ChangeLog';
END
GO

-- v1이 남긴 커밋 불가 행을 닫는다. 화이트리스트 밖 타입은 .sql이 만들어질 수 없어
-- 그대로 두면 목록에 영원히 남는다. v2 트리거는 이런 행을 애초에 만들지 않으므로
-- 이 정리는 옛 행에만 닿고, 여러 번 실행해도 결과가 같다.
-- DBVC_TRACKED_TYPES: 위 트리거의 목록과 같아야 한다. InstallScriptSyncTests가 두 곳을 함께 검사한다.
UPDATE [dbo].[DBVC_ChangeLog]
SET [IsProcessed] = 1
WHERE [IsProcessed] = 0
  AND [ObjectType] NOT IN (N'TABLE', N'VIEW', N'PROCEDURE', N'SQL_STORED_PROCEDURE',
        N'FUNCTION', N'SQL_SCALAR_FUNCTION', N'SQL_TABLE_VALUED_FUNCTION',
        N'SQL_INLINE_TABLE_VALUED_FUNCTION', N'TRIGGER', N'SQL_TRIGGER', N'TYPE',
        N'TABLE_TYPE', N'SEQUENCE OBJECT', N'SEQUENCE_OBJECT', N'SEQUENCE', N'SYNONYM',
        N'INDEX', N'COLUMN');
GO

-- 부모를 모르는 인덱스·컬럼 행은 부모 객체로 정규화할 수 없어 커밋해도 닫히지 않는다.
-- 위 UPDATE와 한 문장으로 합치지 않는 이유는 N'INDEX'가 표식 구간에 두 번 들어가
-- InstallScriptSyncTests의 목록 비교(중복 개수까지 본다)를 깨뜨리기 때문이다.
UPDATE [dbo].[DBVC_ChangeLog]
SET [IsProcessed] = 1
WHERE [IsProcessed] = 0 AND [ObjectType] IN (N'INDEX', N'COLUMN') AND [TargetObjectName] IS NULL;
GO

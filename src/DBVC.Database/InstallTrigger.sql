-- DBVC (DB Version Control) DDL Trigger & ChangeLog Setup Script
-- Target: Microsoft SQL Server
-- 이 스크립트는 멱등(idempotent)하다. 이미 설치된 데이터베이스에 다시 실행해도 안전하며,
-- 구버전 스키마에는 누락된 컬럼만 추가한다.

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

-- 미처리 변경 조회(RefreshState)의 주 조회 경로
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND name = N'IX_DBVC_ChangeLog_IsProcessed')
BEGIN
    CREATE NONCLUSTERED INDEX [IX_DBVC_ChangeLog_IsProcessed]
        ON [dbo].[DBVC_ChangeLog] ([IsProcessed], [PostTime] DESC);
END
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

    -- DBVC_TRACKED_TYPES: ObjectPathConvention.DdlEventObjectTypes + INDEX와 같아야 한다.
    -- InstallScriptSyncTests가 이 목록을 읽어 대조하므로 형식(따옴표 붙은 값 나열)을 바꾸지 말 것.
    -- 여기서 거르지 않으면 사용자·권한 이벤트가 파일 없는 항목으로 목록에 남는다.
    IF @ObjectType NOT IN (N'TABLE', N'VIEW', N'PROCEDURE', N'SQL_STORED_PROCEDURE',
        N'FUNCTION', N'SQL_SCALAR_FUNCTION', N'SQL_TABLE_VALUED_FUNCTION',
        N'SQL_INLINE_TABLE_VALUED_FUNCTION', N'TRIGGER', N'SQL_TRIGGER', N'TYPE',
        N'TABLE_TYPE', N'SEQUENCE OBJECT', N'SEQUENCE_OBJECT', N'SEQUENCE', N'SYNONYM',
        N'INDEX')
        RETURN;

    INSERT INTO [dbo].[DBVC_ChangeLog] (
        [EventType],
        [SchemaName],
        [ObjectName],
        [ObjectType],
        [PostTime],
        [LoginName],
        [TSQLCommand],
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
        0
    );
END;
GO

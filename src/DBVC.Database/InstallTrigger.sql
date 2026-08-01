-- DBVC (DB Version Control) DDL Trigger & ChangeLog Setup Script
-- Target: Microsoft SQL Server
-- 이 스크립트는 멱등(idempotent)하다. 이미 설치된 데이터베이스에 다시 실행해도 안전하며,
-- 구버전 스키마에는 누락된 컬럼만 추가한다.

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
FOR DDL_DATABASE_LEVEL_EVENTS
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        DECLARE @EventData XML;
        SET @EventData = EVENTDATA();

        DECLARE @ObjectName NVARCHAR(256) = @EventData.value('(/EVENT_INSTANCE/ObjectName)[1]', 'NVARCHAR(256)');

        -- DBVC 자체 테이블/트리거에 대한 DDL은 사용자 변경이 아니므로 기록하지 않는다.
        IF @ObjectName IS NULL OR @ObjectName IN (N'DBVC_ChangeLog', N'trg_DBVC_DDL_Tracker')
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
            @EventData.value('(/EVENT_INSTANCE/ObjectType)[1]', 'NVARCHAR(100)'),
            GETDATE(),
            @EventData.value('(/EVENT_INSTANCE/LoginName)[1]', 'NVARCHAR(256)'),
            @EventData.value('(/EVENT_INSTANCE/TSQLCommand/CommandText)[1]', 'NVARCHAR(MAX)'),
            0
        );
    END TRY
    BEGIN CATCH
        -- Suppress trigger errors so database operations do not fail if logging fails
    END CATCH
END;
GO

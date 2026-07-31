-- DBVC (DB Version Control) DDL Trigger & ChangeLog Setup Script
-- Target: Microsoft SQL Server

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[DBVC_ChangeLog] (
        [Id] INT IDENTITY(1,1) PRIMARY KEY,
        [EventType] NVARCHAR(100) NOT NULL,
        [ObjectName] NVARCHAR(256) NOT NULL,
        [ObjectType] NVARCHAR(100) NOT NULL,
        [PostTime] DATETIME NOT NULL DEFAULT GETDATE(),
        [LoginName] NVARCHAR(256) NOT NULL,
        [TSQLCommand] NVARCHAR(MAX) NULL
    );
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

        INSERT INTO [dbo].[DBVC_ChangeLog] (
            [EventType],
            [ObjectName],
            [ObjectType],
            [PostTime],
            [LoginName],
            [TSQLCommand]
        )
        VALUES (
            @EventData.value('(/EVENT_INSTANCE/EventType)[1]', 'NVARCHAR(100)'),
            @EventData.value('(/EVENT_INSTANCE/ObjectName)[1]', 'NVARCHAR(256)'),
            @EventData.value('(/EVENT_INSTANCE/ObjectType)[1]', 'NVARCHAR(100)'),
            GETDATE(),
            @EventData.value('(/EVENT_INSTANCE/LoginName)[1]', 'NVARCHAR(256)'),
            @EventData.value('(/EVENT_INSTANCE/TSQLCommand/CommandText)[1]', 'NVARCHAR(MAX)')
        );
    END TRY
    BEGIN CATCH
        -- Suppress trigger errors so database operations do not fail if logging fails
    END CATCH
END;
GO

-- Migration: Add Telegram message threading support
-- Date: 2025-12-16
-- Purpose: Enable reply threading for signal ? order ? fill ? modify ? close notifications

-- Add TelegramMessageId and NotificationMessageId to ParsedSignalsQueue
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[ParsedSignalsQueue]') AND name = 'TelegramMessageId')
BEGIN
    ALTER TABLE ParsedSignalsQueue
    ADD TelegramMessageId INT NULL;
    
    PRINT 'Added TelegramMessageId column to ParsedSignalsQueue';
END
ELSE
BEGIN
    PRINT 'TelegramMessageId column already exists in ParsedSignalsQueue';
END
GO

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[ParsedSignalsQueue]') AND name = 'NotificationMessageId')
BEGIN
    ALTER TABLE ParsedSignalsQueue
    ADD NotificationMessageId INT NULL;
    
    PRINT 'Added NotificationMessageId column to ParsedSignalsQueue';
END
ELSE
BEGIN
    PRINT 'NotificationMessageId column already exists in ParsedSignalsQueue';
END
GO

-- Add TelegramMessageId to ForexTrades for threading fill/modify/close notifications
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[ForexTrades]') AND name = 'TelegramMessageId')
BEGIN
    ALTER TABLE ForexTrades
    ADD TelegramMessageId INT NULL;
    
    PRINT 'Added TelegramMessageId column to ForexTrades';
END
ELSE
BEGIN
    PRINT 'TelegramMessageId column already exists in ForexTrades';
END
GO

-- Verify the changes
SELECT 'ParsedSignalsQueue' AS TableName, COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'ParsedSignalsQueue'
  AND COLUMN_NAME IN ('TelegramMessageId', 'NotificationMessageId')
UNION ALL
SELECT 'ForexTrades' AS TableName, COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'ForexTrades'
  AND COLUMN_NAME = 'TelegramMessageId';
GO

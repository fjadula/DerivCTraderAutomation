-- Create ParsedSignalsQueue table if it doesn't exist
-- This table stores all parsed signals from Telegram channels before execution

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ParsedSignalsQueue')
BEGIN
    CREATE TABLE ParsedSignalsQueue (
        SignalId INT IDENTITY(1,1) PRIMARY KEY,
        Asset NVARCHAR(20) NOT NULL,
        Direction NVARCHAR(10) NOT NULL,
        EntryPrice DECIMAL(18,5) NULL,
        StopLoss DECIMAL(18,5) NULL,
        TakeProfit DECIMAL(18,5) NULL,
        TakeProfit2 DECIMAL(18,5) NULL,
        TakeProfit3 DECIMAL(18,5) NULL,
        TakeProfit4 DECIMAL(18,5) NULL,
        RiskRewardRatio DECIMAL(10,2) NULL,
        LotSize DECIMAL(10,2) NULL,
        ProviderChannelId NVARCHAR(50) NULL,
        ProviderName NVARCHAR(100) NULL,
        SignalType NVARCHAR(20) NOT NULL,
        Pattern NVARCHAR(100) NULL,
        Timeframe NVARCHAR(10) NULL,
        RawMessage NVARCHAR(MAX) NULL,
        ReceivedAt DATETIME NOT NULL DEFAULT GETUTCDATE(),
        Processed BIT NOT NULL DEFAULT 0,
        ProcessedAt DATETIME NULL,
        
        INDEX IX_ParsedSignalsQueue_Processed (Processed, ReceivedAt),
        INDEX IX_ParsedSignalsQueue_Provider (ProviderChannelId, ReceivedAt)
    );
    
    PRINT 'ParsedSignalsQueue table created successfully';
END
ELSE
BEGIN
    PRINT 'ParsedSignalsQueue table already exists';
END
GO

-- Verify the table structure
SELECT 
    COLUMN_NAME,
    DATA_TYPE,
    CHARACTER_MAXIMUM_LENGTH,
    IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'ParsedSignalsQueue'
ORDER BY ORDINAL_POSITION;
GO

-- Query to check recent signals
SELECT TOP 10 
    SignalId,
    Asset,
    Direction,
    EntryPrice,
    ProviderName,
    SignalType,
    Processed,
    ReceivedAt
FROM ParsedSignalsQueue
ORDER BY ReceivedAt DESC;
GO

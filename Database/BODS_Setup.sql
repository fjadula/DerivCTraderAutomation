-- =====================================================
-- BODS Database Setup Script
-- Binary Options Daily Signals
-- =====================================================

-- Create the database if it doesn't exist
IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'BODS')
BEGIN
    CREATE DATABASE BODS;
END
GO

USE BODS;
GO

-- =====================================================
-- Table: ProviderChannelConfig
-- Stores provider metadata and execution settings
-- =====================================================
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ProviderChannelConfig')
BEGIN
    CREATE TABLE [dbo].[ProviderChannelConfig] (
        [ProviderChannelId] NVARCHAR(50) NOT NULL PRIMARY KEY,
        [ProviderName] NVARCHAR(100) NOT NULL,
        [TakeOriginal] BIT NOT NULL DEFAULT 1,
        [TakeOpposite] BIT NOT NULL DEFAULT 0,
        [ExpiryTime] INT NOT NULL DEFAULT 40,  -- In minutes
        [DefaultAsset] NVARCHAR(20) NULL,      -- For providers that don't specify asset
        [IsActive] BIT NOT NULL DEFAULT 1,
        [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE()
    );
END
GO

-- =====================================================
-- Table: ParsedSignalsQueue
-- Buffer for signals waiting to be executed
-- =====================================================
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ParsedSignalsQueue')
BEGIN
    CREATE TABLE [dbo].[ParsedSignalsQueue] (
        [SignalId] INT IDENTITY(1,1) PRIMARY KEY,
        [Asset] NVARCHAR(50) NOT NULL,
        [Direction] NVARCHAR(10) NOT NULL,
        [EntryPrice] DECIMAL(18,5) NULL,
        [StopLoss] DECIMAL(18,5) NULL,
        [TakeProfit] DECIMAL(18,5) NULL,
        [TakeProfit2] DECIMAL(18,5) NULL,
        [TakeProfit3] DECIMAL(18,5) NULL,
        [TakeProfit4] DECIMAL(18,5) NULL,
        [ProviderChannelId] NVARCHAR(50) NOT NULL,
        [ProviderName] NVARCHAR(100) NULL,
        [ReceivedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        [Processed] BIT NOT NULL DEFAULT 0,
        [ProcessedAt] DATETIME2 NULL,
        [RawMessage] NVARCHAR(MAX) NULL,
        [TelegramMessageId] INT NULL
    );

    -- Unique constraint to prevent duplicate signals
    CREATE UNIQUE INDEX UQ_ParsedSignals_Unique
    ON ParsedSignalsQueue(Asset, Direction, ProviderChannelId, EntryPrice)
    WHERE EntryPrice IS NOT NULL;

    -- Index for polling unprocessed signals
    CREATE INDEX IX_ParsedSignals_Unprocessed
    ON ParsedSignalsQueue(Processed, ReceivedAt)
    WHERE Processed = 0;

    -- Index for provider lookup
    CREATE INDEX IX_ParsedSignals_Provider
    ON ParsedSignalsQueue(ProviderChannelId);
END
GO

-- =====================================================
-- Table: BinaryOptionTrades
-- Records all executed binary trades and their results
-- =====================================================
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'BinaryOptionTrades')
BEGIN
    CREATE TABLE [dbo].[BinaryOptionTrades] (
        [TradeId] INT IDENTITY(1,1) PRIMARY KEY,
        [AssetName] NVARCHAR(50) NOT NULL,
        [Direction] NVARCHAR(10) NOT NULL,           -- CALL or PUT
        [OpenTime] DATETIME2 NULL,
        [CloseTime] DATETIME2 NULL,
        [ExpiryLength] INT NOT NULL,                 -- In minutes
        [Result] NVARCHAR(20) NULL,                  -- WIN, LOSS, TIE, PENDING
        [ClosedBeforeExpiry] BIT NOT NULL DEFAULT 0,
        [SentToTelegramPublic] BIT NOT NULL DEFAULT 0,
        [SentToTelegramPrivate] BIT NOT NULL DEFAULT 0,
        [SentToWhatsApp] BIT NOT NULL DEFAULT 0,
        [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        [ExpiryDisplay] NVARCHAR(20) NULL,           -- '5M', '40M', '2H'
        [TradeStake] DECIMAL(18,2) NOT NULL DEFAULT 1.00,
        [ExpectedExpiryTimestamp] DATETIME2 NULL,
        [EntryPrice] DECIMAL(18,5) NULL,
        [ExitPrice] DECIMAL(18,5) NULL,
        [StrategyName] NVARCHAR(100) NULL,           -- Provider name
        [PredictedOutcome] NVARCHAR(20) NULL,
        [PredictionConfidence] DECIMAL(5,2) NULL,
        [SignalGeneratedAt] DATETIME2 NULL,
        [SignalValidUntil] DATETIME2 NULL,
        [SentOpenToTelegram] BIT NOT NULL DEFAULT 0,
        [SentCloseToTelegram] BIT NOT NULL DEFAULT 0,
        [TelegramMessageId] INT NULL,
        [DerivContractId] NVARCHAR(50) NULL,
        [ProviderChannelId] NVARCHAR(50) NULL,
        [RawSignalText] NVARCHAR(MAX) NULL,
        [IsOpposite] BIT NOT NULL DEFAULT 0,         -- Was this the opposite trade?
        [SignalId] INT NULL                          -- Link to original signal
    );

    CREATE INDEX IX_BinaryOptionTrades_CreatedAt
    ON BinaryOptionTrades(CreatedAt DESC);

    CREATE INDEX IX_BinaryOptionTrades_Result
    ON BinaryOptionTrades(Result);

    CREATE INDEX IX_BinaryOptionTrades_Provider
    ON BinaryOptionTrades(ProviderChannelId);

    CREATE INDEX IX_BinaryOptionTrades_Pending
    ON BinaryOptionTrades(Result, ExpectedExpiryTimestamp)
    WHERE Result = 'PENDING';

    CREATE INDEX IX_BinaryOptionTrades_ContractId
    ON BinaryOptionTrades(DerivContractId)
    WHERE DerivContractId IS NOT NULL;
END
GO

-- =====================================================
-- Table: ForexTrades (Future Use)
-- For potential forex signal tracking
-- =====================================================
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ForexTrades')
BEGIN
    CREATE TABLE [dbo].[ForexTrades] (
        [TradeId] INT IDENTITY(1,1) PRIMARY KEY,
        [Symbol] NVARCHAR(20) NOT NULL,
        [Direction] NVARCHAR(10) NOT NULL,
        [EntryPrice] DECIMAL(18,5) NULL,
        [ExitPrice] DECIMAL(18,5) NULL,
        [EntryTime] DATETIME2 NULL,
        [ExitTime] DATETIME2 NULL,
        [PnL] DECIMAL(18,2) NULL,
        [PnLPercent] DECIMAL(10,4) NULL,
        [Status] NVARCHAR(20) NOT NULL DEFAULT 'PENDING',
        [Notes] NVARCHAR(MAX) NULL,
        [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        [IndicatorsLinked] NVARCHAR(MAX) NULL,
        [StrategyName] NVARCHAR(100) NULL,
        [OriginalDirection] NVARCHAR(10) NULL,
        [FinalDirection] NVARCHAR(10) NULL,
        [StopLoss] DECIMAL(18,5) NULL,
        [TakeProfit] DECIMAL(18,5) NULL,
        [RRAtClose] DECIMAL(10,4) NULL,
        [CloseReason] NVARCHAR(100) NULL,
        [SentOpenToTelegramPrivate] BIT NOT NULL DEFAULT 0,
        [SentCloseToTelegramPrivate] BIT NOT NULL DEFAULT 0,
        [SentToTelegramPublic] BIT NOT NULL DEFAULT 0,
        [SentToWhatsApp] BIT NOT NULL DEFAULT 0,
        [PositionId] NVARCHAR(50) NULL,
        [Strategy] NVARCHAR(100) NULL,
        [Outcome] NVARCHAR(20) NULL,
        [TP] DECIMAL(18,5) NULL,
        [SL] DECIMAL(18,5) NULL,
        [RR] DECIMAL(10,4) NULL,
        [TelegramMessageId] INT NULL,
        [ProviderChannelId] NVARCHAR(50) NULL
    );

    CREATE INDEX IX_ForexTrades_CreatedAt
    ON ForexTrades(CreatedAt DESC);

    CREATE INDEX IX_ForexTrades_Status
    ON ForexTrades(Status);
END
GO

-- =====================================================
-- Insert Initial Provider Configurations
-- =====================================================

-- Clear existing (for re-runs)
-- DELETE FROM ProviderChannelConfig;

-- JAMSONTRADER
IF NOT EXISTS (SELECT 1 FROM ProviderChannelConfig WHERE ProviderChannelId = '-1001641563130')
BEGIN
    INSERT INTO ProviderChannelConfig
        (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite, ExpiryTime, DefaultAsset, IsActive)
    VALUES
        ('-1001641563130', 'JAMSONTRADER', 1, 0, 40, NULL, 1);
END

-- FXPIPSPREDATOR (Note: DefaultAsset is XAUUSD since signals don't include asset)
IF NOT EXISTS (SELECT 1 FROM ProviderChannelConfig WHERE ProviderChannelId = '-1001235677912')
BEGIN
    INSERT INTO ProviderChannelConfig
        (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite, ExpiryTime, DefaultAsset, IsActive)
    VALUES
        ('-1001235677912', 'FXPIPSPREDATOR', 1, 0, 40, 'XAUUSD', 1);
END

-- FOREXSIGNALSIO
IF NOT EXISTS (SELECT 1 FROM ProviderChannelConfig WHERE ProviderChannelId = '-1001768939027')
BEGIN
    INSERT INTO ProviderChannelConfig
        (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite, ExpiryTime, DefaultAsset, IsActive)
    VALUES
        ('-1001768939027', 'FOREXSIGNALSIO', 1, 0, 40, NULL, 1);
END

-- XAUUSD GOLD SIGNAL
IF NOT EXISTS (SELECT 1 FROM ProviderChannelConfig WHERE ProviderChannelId = '-1001419177246')
BEGIN
    INSERT INTO ProviderChannelConfig
        (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite, ExpiryTime, DefaultAsset, IsActive)
    VALUES
        ('-1001419177246', 'XAUUSD_GOLD_SIGNAL', 1, 0, 40, NULL, 1);
END

-- TRADEMASTER
IF NOT EXISTS (SELECT 1 FROM ProviderChannelConfig WHERE ProviderChannelId = '-1002626150817')
BEGIN
    INSERT INTO ProviderChannelConfig
        (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite, ExpiryTime, DefaultAsset, IsActive)
    VALUES
        ('-1002626150817', 'TRADEMASTER', 1, 0, 40, NULL, 1);
END

GO

-- =====================================================
-- Verify Setup
-- =====================================================
SELECT 'ProviderChannelConfig' AS TableName, COUNT(*) AS RowCount FROM ProviderChannelConfig
UNION ALL
SELECT 'ParsedSignalsQueue', COUNT(*) FROM ParsedSignalsQueue
UNION ALL
SELECT 'BinaryOptionTrades', COUNT(*) FROM BinaryOptionTrades
UNION ALL
SELECT 'ForexTrades', COUNT(*) FROM ForexTrades;

SELECT * FROM ProviderChannelConfig;

PRINT 'BODS Database setup completed successfully!';
GO

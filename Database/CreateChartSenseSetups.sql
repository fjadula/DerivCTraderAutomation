-- ChartSense Setups Table
-- Stateful tracking for image-based ChartSense signals
-- One active setup per asset constraint enforced by unique filtered index

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ChartSenseSetups')
BEGIN
    CREATE TABLE ChartSenseSetups (
        SetupId INT IDENTITY(1,1) PRIMARY KEY,

        -- Asset identification (one active setup per asset)
        Asset NVARCHAR(20) NOT NULL,

        -- Signal properties from image OCR
        Direction NVARCHAR(10) NOT NULL,  -- 'Buy' or 'Sell'
        Timeframe NVARCHAR(10) NOT NULL,  -- 'H1', 'H2', 'H4', 'D1', 'M15', 'M30'
        PatternType NVARCHAR(50) NOT NULL, -- 'Trendline', 'Wedge', 'Rectangle', 'Breakout', etc.
        PatternClassification NVARCHAR(20) NOT NULL, -- 'Reaction' or 'Breakout'

        -- Derived entry levels from chart analysis
        EntryPrice DECIMAL(18,5) NULL,
        EntryZoneMin DECIMAL(18,5) NULL,  -- Entry zone lower bound
        EntryZoneMax DECIMAL(18,5) NULL,  -- Entry zone upper bound

        -- cTrader order tracking
        CTraderOrderId BIGINT NULL,
        CTraderPositionId BIGINT NULL,

        -- Status tracking
        Status NVARCHAR(20) NOT NULL DEFAULT 'Watching',
        -- Values: Watching, PendingPlaced, Filled, Closed, Expired, Invalidated

        -- Timeout tracking
        TimeoutAt DATETIME NULL,  -- Calculated based on timeframe

        -- Source signal reference
        SignalId INT NULL,  -- FK to ParsedSignalsQueue
        TelegramMessageId INT NULL,

        -- Deriv binary execution (after forex fill)
        DerivContractId NVARCHAR(100) NULL,
        DerivExpiryMinutes INT NULL,

        -- Timestamps
        CreatedAt DATETIME NOT NULL DEFAULT GETUTCDATE(),
        UpdatedAt DATETIME NOT NULL DEFAULT GETUTCDATE(),
        ClosedAt DATETIME NULL,

        -- Image analysis metadata
        ImageHash NVARCHAR(64) NULL,  -- SHA256 hash to detect duplicate images
        CalibrationData NVARCHAR(MAX) NULL,  -- JSON: Y-axis calibration params
        DetectedLines NVARCHAR(MAX) NULL,  -- JSON: Detected support/resistance lines

        -- Provider info
        ProviderChannelId NVARCHAR(50) NOT NULL,

        -- Indexes for common queries
        INDEX IX_ChartSenseSetups_Asset_Status (Asset, Status),
        INDEX IX_ChartSenseSetups_Status_TimeoutAt (Status, TimeoutAt),
        INDEX IX_ChartSenseSetups_ProviderChannelId (ProviderChannelId),
        INDEX IX_ChartSenseSetups_CTraderOrderId (CTraderOrderId) WHERE CTraderOrderId IS NOT NULL,
        INDEX IX_ChartSenseSetups_CTraderPositionId (CTraderPositionId) WHERE CTraderPositionId IS NOT NULL
    );

    PRINT 'ChartSenseSetups table created successfully';
END
GO

-- Unique constraint: only one non-terminal setup per asset
-- This enforces the "one active setup per asset" rule
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_ChartSenseSetups_ActiveAsset')
BEGIN
    CREATE UNIQUE INDEX UX_ChartSenseSetups_ActiveAsset
    ON ChartSenseSetups (Asset)
    WHERE Status IN ('Watching', 'PendingPlaced', 'Filled');

    PRINT 'Unique index UX_ChartSenseSetups_ActiveAsset created';
END
GO

-- Insert ChartSense provider configuration if not exists
IF NOT EXISTS (SELECT 1 FROM ProviderChannelConfig WHERE ProviderChannelId = '-1001200022443')
BEGIN
    INSERT INTO ProviderChannelConfig (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite, IsActive, CreatedAt)
    VALUES ('-1001200022443', 'ChartSense', 1, 0, 1, GETUTCDATE());

    PRINT 'ChartSense provider configuration inserted';
END
GO

-- Verify creation
SELECT 'ChartSenseSetups' AS TableName, COUNT(*) AS RowCount FROM ChartSenseSetups;
SELECT * FROM ProviderChannelConfig WHERE ProviderChannelId = '-1001200022443';
GO

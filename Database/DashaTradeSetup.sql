-- =============================================
-- Dasha Trade Selective Martingale Execution
-- Database Setup Script
-- =============================================

-- =============================================
-- Table: DashaProviderConfig
-- Per-provider martingale settings (extensible)
-- =============================================
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'DashaProviderConfig')
BEGIN
    CREATE TABLE DashaProviderConfig (
        ConfigId INT IDENTITY(1,1) PRIMARY KEY,
        ProviderChannelId NVARCHAR(50) NOT NULL,
        ProviderName NVARCHAR(100) NOT NULL,

        -- Martingale configuration
        InitialStake DECIMAL(10,2) NOT NULL DEFAULT 50.00,
        LadderSteps NVARCHAR(100) NOT NULL DEFAULT '50,100,200',  -- Comma-separated stake ladder
        ResetAfterStep INT NOT NULL DEFAULT 3,                     -- Reset after this many wins OR any loss at max

        -- Signal configuration
        DefaultExpiryMinutes INT NOT NULL DEFAULT 15,

        -- Flags
        IsActive BIT NOT NULL DEFAULT 1,
        ExecuteOnProviderLoss BIT NOT NULL DEFAULT 1,   -- Core: only execute when provider loses

        -- Metadata
        CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),

        CONSTRAINT UQ_DashaProviderConfig_ChannelId UNIQUE (ProviderChannelId)
    );

    PRINT 'Created table: DashaProviderConfig';
END
GO

-- =============================================
-- Table: DashaPendingSignals
-- Signals awaiting expiry evaluation
-- =============================================
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'DashaPendingSignals')
BEGIN
    CREATE TABLE DashaPendingSignals (
        PendingSignalId INT IDENTITY(1,1) PRIMARY KEY,
        ProviderChannelId NVARCHAR(50) NOT NULL,
        ProviderName NVARCHAR(100) NOT NULL,

        -- Signal details
        Asset NVARCHAR(20) NOT NULL,                    -- e.g., "USDJPY"
        Direction NVARCHAR(10) NOT NULL,                -- "UP" or "DOWN" (provider's call)
        Timeframe NVARCHAR(10) NOT NULL,                -- e.g., "M15", "M5"
        ExpiryMinutes INT NOT NULL,                     -- Derived from timeframe (15, 5, etc.)

        -- Price snapshots
        EntryPrice DECIMAL(18,6) NOT NULL,              -- Spot at signal receipt
        ExitPrice DECIMAL(18,6) NULL,                   -- Spot at expiry (filled after wait)

        -- Timing
        SignalReceivedAt DATETIME2 NOT NULL,            -- When signal arrived
        ExpiryAt DATETIME2 NOT NULL,                    -- SignalReceivedAt + ExpiryMinutes
        EvaluatedAt DATETIME2 NULL,                     -- When we fetched exit price

        -- Evaluation result
        Status NVARCHAR(20) NOT NULL DEFAULT 'AwaitingExpiry',
        -- Values: AwaitingExpiry, ProviderWon, ProviderLost, Executed, Error
        ProviderResult NVARCHAR(10) NULL,               -- "Won" or "Lost"

        -- Raw data
        TelegramMessageId INT NULL,
        RawMessage NVARCHAR(MAX) NULL,

        -- Metadata
        CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
    );

    -- Indexes for efficient polling
    CREATE INDEX IX_DashaPendingSignals_Status ON DashaPendingSignals (Status);
    CREATE INDEX IX_DashaPendingSignals_ExpiryAt ON DashaPendingSignals (ExpiryAt) WHERE Status = 'AwaitingExpiry';
    CREATE INDEX IX_DashaPendingSignals_Provider ON DashaPendingSignals (ProviderChannelId);

    PRINT 'Created table: DashaPendingSignals';
END
GO

-- =============================================
-- Table: DashaCompoundingState
-- Persistent compounding ladder state per provider
-- =============================================
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'DashaCompoundingState')
BEGIN
    CREATE TABLE DashaCompoundingState (
        StateId INT IDENTITY(1,1) PRIMARY KEY,
        ProviderChannelId NVARCHAR(50) NOT NULL,

        -- Current position on ladder (0-indexed)
        CurrentStep INT NOT NULL DEFAULT 0,             -- 0 = $50, 1 = $100, 2 = $200
        CurrentStake DECIMAL(10,2) NOT NULL,            -- Current stake amount

        -- Statistics
        ConsecutiveWins INT NOT NULL DEFAULT 0,
        TotalWins INT NOT NULL DEFAULT 0,
        TotalLosses INT NOT NULL DEFAULT 0,
        TotalProfit DECIMAL(18,2) NOT NULL DEFAULT 0,

        -- Last update
        LastTradeAt DATETIME2 NULL,
        UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),

        CONSTRAINT UQ_DashaCompoundingState_ChannelId UNIQUE (ProviderChannelId)
    );

    CREATE INDEX IX_DashaCompoundingState_Provider ON DashaCompoundingState (ProviderChannelId);

    PRINT 'Created table: DashaCompoundingState';
END
GO

-- =============================================
-- Table: DashaTrades
-- Executed trades with full audit trail
-- =============================================
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'DashaTrades')
BEGIN
    CREATE TABLE DashaTrades (
        TradeId INT IDENTITY(1,1) PRIMARY KEY,
        PendingSignalId INT NOT NULL,
        ProviderChannelId NVARCHAR(50) NOT NULL,
        ProviderName NVARCHAR(100) NOT NULL,

        -- Trade details
        Asset NVARCHAR(20) NOT NULL,
        Direction NVARCHAR(10) NOT NULL,                -- "CALL" or "PUT"
        ExpiryMinutes INT NOT NULL,

        -- Deriv execution
        DerivContractId NVARCHAR(50) NULL,              -- NULL until executed
        Stake DECIMAL(10,2) NOT NULL,
        StakeStep INT NOT NULL,                         -- 0, 1, or 2 (position on ladder)
        PurchasePrice DECIMAL(18,6) NULL,
        Payout DECIMAL(18,6) NULL,

        -- Provider context
        ProviderEntryPrice DECIMAL(18,6) NOT NULL,      -- Provider's entry snapshot
        ProviderExitPrice DECIMAL(18,6) NOT NULL,       -- Provider's exit price at expiry
        ProviderResult NVARCHAR(10) NOT NULL,           -- "Lost" (always, since we only trade on loss)

        -- Our execution result
        ExecutionResult NVARCHAR(10) NULL,              -- "Won" or "Lost"
        Profit DECIMAL(18,6) NULL,

        -- Timing
        ProviderSignalAt DATETIME2 NOT NULL,
        ProviderExpiryAt DATETIME2 NOT NULL,
        ExecutedAt DATETIME2 NULL,
        SettledAt DATETIME2 NULL,

        -- Telegram notification
        TelegramMessageId INT NULL,

        -- Metadata
        CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),

        CONSTRAINT FK_DashaTrades_PendingSignal FOREIGN KEY (PendingSignalId)
            REFERENCES DashaPendingSignals(PendingSignalId)
    );

    CREATE INDEX IX_DashaTrades_Provider ON DashaTrades (ProviderChannelId);
    CREATE INDEX IX_DashaTrades_Settled ON DashaTrades (SettledAt) WHERE SettledAt IS NULL;
    CREATE INDEX IX_DashaTrades_Result ON DashaTrades (ExecutionResult);
    CREATE INDEX IX_DashaTrades_ContractId ON DashaTrades (DerivContractId) WHERE DerivContractId IS NOT NULL;

    PRINT 'Created table: DashaTrades';
END
GO

-- =============================================
-- Insert initial Dasha Trade provider config
-- =============================================
IF NOT EXISTS (SELECT 1 FROM DashaProviderConfig WHERE ProviderChannelId = '-1001570351142')
BEGIN
    INSERT INTO DashaProviderConfig
    (ProviderChannelId, ProviderName, InitialStake, LadderSteps, ResetAfterStep, DefaultExpiryMinutes, IsActive, ExecuteOnProviderLoss)
    VALUES
    ('-1001570351142', 'DashaTrade', 50.00, '50,100,200', 3, 15, 1, 1);

    PRINT 'Inserted initial config for DashaTrade provider';
END
GO

-- =============================================
-- Initialize compounding state for Dasha Trade
-- =============================================
IF NOT EXISTS (SELECT 1 FROM DashaCompoundingState WHERE ProviderChannelId = '-1001570351142')
BEGIN
    INSERT INTO DashaCompoundingState
    (ProviderChannelId, CurrentStep, CurrentStake, ConsecutiveWins, TotalWins, TotalLosses, TotalProfit)
    VALUES
    ('-1001570351142', 0, 50.00, 0, 0, 0, 0);

    PRINT 'Initialized compounding state for DashaTrade provider';
END
GO

PRINT 'Dasha Trade database setup complete!';

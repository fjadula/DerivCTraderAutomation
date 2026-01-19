-- DowOpenGS Strategy Tables
-- Creates tables for strategy control, parameters, signal logging, and previous close caching

-- ===== STRATEGY CONTROL TABLE =====
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'StrategyControl')
BEGIN
    CREATE TABLE StrategyControl (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        StrategyName NVARCHAR(50) NOT NULL,
        IsEnabled BIT NOT NULL DEFAULT 0,
        ExecuteCFD BIT NOT NULL DEFAULT 0,
        ExecuteBinary BIT NOT NULL DEFAULT 0,
        ExecuteMT5 BIT NOT NULL DEFAULT 0,
        DryRun BIT NOT NULL DEFAULT 1,  -- Default to dry run for safety
        CreatedAt DATETIME NOT NULL DEFAULT GETUTCDATE(),
        UpdatedAt DATETIME NOT NULL DEFAULT GETUTCDATE(),

        CONSTRAINT UQ_StrategyControl_Name UNIQUE (StrategyName)
    );

    PRINT 'StrategyControl table created successfully';
END
GO

-- ===== STRATEGY PARAMETERS TABLE =====
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'StrategyParameters')
BEGIN
    CREATE TABLE StrategyParameters (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        StrategyName NVARCHAR(50) NOT NULL,
        ParameterName NVARCHAR(100) NOT NULL,
        ParameterValue NVARCHAR(500) NOT NULL,
        Description NVARCHAR(500) NULL,
        CreatedAt DATETIME NOT NULL DEFAULT GETUTCDATE(),
        UpdatedAt DATETIME NOT NULL DEFAULT GETUTCDATE(),

        CONSTRAINT UQ_StrategyParameters_NameParam UNIQUE (StrategyName, ParameterName),
        INDEX IX_StrategyParameters_Strategy (StrategyName)
    );

    PRINT 'StrategyParameters table created successfully';
END
GO

-- ===== DOW OPEN GS SIGNALS TABLE =====
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'DowOpenGSSignals')
BEGIN
    CREATE TABLE DowOpenGSSignals (
        SignalId INT IDENTITY(1,1) PRIMARY KEY,
        TradeDate DATE NOT NULL,

        -- Goldman Sachs data
        GS_PreviousClose DECIMAL(18,4) NOT NULL,
        GS_LatestPrice DECIMAL(18,4) NOT NULL,
        GS_Direction NVARCHAR(10) NOT NULL,
        GS_Change DECIMAL(18,4) NOT NULL,

        -- Dow Futures data
        YM_PreviousClose DECIMAL(18,4) NOT NULL,
        YM_LatestPrice DECIMAL(18,4) NOT NULL,
        YM_Direction NVARCHAR(10) NOT NULL,
        YM_Change DECIMAL(18,4) NOT NULL,

        -- Signal result
        FinalSignal NVARCHAR(10) NOT NULL,
        BinaryExpiry INT NOT NULL,
        NoTradeReason NVARCHAR(200) NULL,

        -- Execution status
        WasDryRun BIT NOT NULL DEFAULT 0,
        CFDExecuted BIT NOT NULL DEFAULT 0,
        BinaryExecuted BIT NOT NULL DEFAULT 0,
        CFDOrderId NVARCHAR(100) NULL,
        BinaryContractId NVARCHAR(100) NULL,
        CFDEntryPrice DECIMAL(18,4) NULL,
        CFDStopLoss DECIMAL(18,4) NULL,
        CFDTakeProfit DECIMAL(18,4) NULL,

        -- Timestamps
        SnapshotAt DATETIME NOT NULL,
        ExecutedAt DATETIME NULL,
        CreatedAt DATETIME NOT NULL DEFAULT GETUTCDATE(),

        -- Error tracking
        ErrorMessage NVARCHAR(1000) NULL,

        -- Indexes
        INDEX IX_DowOpenGSSignals_TradeDate (TradeDate),
        INDEX IX_DowOpenGSSignals_FinalSignal (FinalSignal),
        INDEX IX_DowOpenGSSignals_CreatedAt (CreatedAt)
    );

    PRINT 'DowOpenGSSignals table created successfully';
END
GO

-- ===== MARKET PREVIOUS CLOSE TABLE =====
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'MarketPreviousClose')
BEGIN
    CREATE TABLE MarketPreviousClose (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Symbol NVARCHAR(20) NOT NULL,
        PreviousClose DECIMAL(18,4) NOT NULL,
        CloseDate DATE NOT NULL,
        CachedAt DATETIME NOT NULL DEFAULT GETUTCDATE(),
        DataSource NVARCHAR(50) NOT NULL DEFAULT 'YahooFinance',

        -- Only one record per symbol per date
        CONSTRAINT UQ_MarketPreviousClose_SymbolDate UNIQUE (Symbol, CloseDate),
        INDEX IX_MarketPreviousClose_Symbol (Symbol),
        INDEX IX_MarketPreviousClose_CloseDate (CloseDate)
    );

    PRINT 'MarketPreviousClose table created successfully';
END
GO

-- ===== NYSE HOLIDAYS TABLE (for skipping execution) =====
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'USMarketHolidays')
BEGIN
    CREATE TABLE USMarketHolidays (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        HolidayDate DATE NOT NULL,
        HolidayName NVARCHAR(100) NOT NULL,

        CONSTRAINT UQ_USMarketHolidays_Date UNIQUE (HolidayDate)
    );

    PRINT 'USMarketHolidays table created successfully';
END
GO

-- ===== INSERT DEFAULT DOWOPENGS STRATEGY CONTROL =====
IF NOT EXISTS (SELECT 1 FROM StrategyControl WHERE StrategyName = 'DowOpenGS')
BEGIN
    INSERT INTO StrategyControl (StrategyName, IsEnabled, ExecuteCFD, ExecuteBinary, ExecuteMT5, DryRun)
    VALUES ('DowOpenGS', 0, 0, 0, 0, 1);  -- Disabled and DryRun by default

    PRINT 'DowOpenGS strategy control inserted (disabled, dry run)';
END
GO

-- ===== INSERT DEFAULT DOWOPENGS PARAMETERS =====
IF NOT EXISTS (SELECT 1 FROM StrategyParameters WHERE StrategyName = 'DowOpenGS')
BEGIN
    INSERT INTO StrategyParameters (StrategyName, ParameterName, ParameterValue, Description) VALUES
    ('DowOpenGS', 'BinaryStakeUSD', '10', 'Binary options stake in USD'),
    ('DowOpenGS', 'CFDVolume', '1.0', 'CFD lot size/volume'),
    ('DowOpenGS', 'CFDStopLossPercent', '0.35', 'CFD Stop Loss percentage'),
    ('DowOpenGS', 'CFDTakeProfitPercent', '0.70', 'CFD Take Profit percentage'),
    ('DowOpenGS', 'CFDMaxHoldMinutes', '30', 'CFD max hold time in minutes'),
    ('DowOpenGS', 'DefaultBinaryExpiry', '15', 'Default binary expiry in minutes'),
    ('DowOpenGS', 'ExtendedBinaryExpiry', '30', 'Extended binary expiry in minutes'),
    ('DowOpenGS', 'MinGSMoveForExtendedExpiry', '3.0', 'Min GS move (USD) for extended expiry'),
    ('DowOpenGS', 'SnapshotOffsetSeconds', '10', 'Seconds before market open to take snapshot'),
    ('DowOpenGS', 'DerivCFDSymbol', 'WALLSTREET30', 'Deriv symbol for Wall Street 30 CFD'),
    ('DowOpenGS', 'DerivBinarySymbol', 'WALLSTREET30', 'Deriv symbol for Wall Street 30 Binary');

    PRINT 'DowOpenGS default parameters inserted';
END
GO

-- ===== INSERT 2025 NYSE HOLIDAYS =====
IF NOT EXISTS (SELECT 1 FROM USMarketHolidays WHERE YEAR(HolidayDate) = 2025)
BEGIN
    INSERT INTO USMarketHolidays (HolidayDate, HolidayName) VALUES
    ('2025-01-01', 'New Year''s Day'),
    ('2025-01-20', 'Martin Luther King Jr. Day'),
    ('2025-02-17', 'Presidents'' Day'),
    ('2025-04-18', 'Good Friday'),
    ('2025-05-26', 'Memorial Day'),
    ('2025-06-19', 'Juneteenth'),
    ('2025-07-04', 'Independence Day'),
    ('2025-09-01', 'Labor Day'),
    ('2025-11-27', 'Thanksgiving Day'),
    ('2025-12-25', 'Christmas Day');

    PRINT '2025 NYSE holidays inserted';
END
GO

-- ===== VERIFY CREATION =====
SELECT 'StrategyControl' AS TableName, COUNT(*) AS RecordCount FROM StrategyControl;
SELECT 'StrategyParameters' AS TableName, COUNT(*) AS RecordCount FROM StrategyParameters;
SELECT 'DowOpenGSSignals' AS TableName, COUNT(*) AS RecordCount FROM DowOpenGSSignals;
SELECT 'MarketPreviousClose' AS TableName, COUNT(*) AS RecordCount FROM MarketPreviousClose;
SELECT 'USMarketHolidays' AS TableName, COUNT(*) AS RecordCount FROM USMarketHolidays;

-- Show strategy control
SELECT * FROM StrategyControl WHERE StrategyName = 'DowOpenGS';

-- Show strategy parameters
SELECT * FROM StrategyParameters WHERE StrategyName = 'DowOpenGS';
GO

-- DowOpenGS Backtesting Tables
-- Stores historical price data and backtest results

-- ===== MARKET PRICE HISTORY TABLE =====
-- Stores 1-minute candle data for backtesting
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'MarketPriceHistory')
BEGIN
    CREATE TABLE MarketPriceHistory (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        Symbol NVARCHAR(20) NOT NULL,
        TimeUtc DATETIME NOT NULL,
        [Open] DECIMAL(18,4) NOT NULL,
        High DECIMAL(18,4) NOT NULL,
        Low DECIMAL(18,4) NOT NULL,
        [Close] DECIMAL(18,4) NOT NULL,
        Volume BIGINT NULL,
        DataSource NVARCHAR(50) NOT NULL DEFAULT 'YahooFinance',

        -- Unique constraint: one candle per symbol per minute
        CONSTRAINT UQ_MarketPriceHistory_SymbolTime UNIQUE (Symbol, TimeUtc),

        -- Indexes for efficient querying
        INDEX IX_MarketPriceHistory_Symbol (Symbol),
        INDEX IX_MarketPriceHistory_TimeUtc (TimeUtc),
        INDEX IX_MarketPriceHistory_SymbolTimeUtc (Symbol, TimeUtc)
    );

    PRINT 'MarketPriceHistory table created successfully';
END
GO

-- ===== BACKTEST RUNS TABLE =====
-- Tracks each backtest execution
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'BacktestRuns')
BEGIN
    CREATE TABLE BacktestRuns (
        RunId INT IDENTITY(1,1) PRIMARY KEY,
        StrategyName NVARCHAR(50) NOT NULL,
        StartDate DATE NOT NULL,
        EndDate DATE NOT NULL,

        -- Summary statistics
        TotalTradingDays INT NOT NULL DEFAULT 0,
        TotalSignals INT NOT NULL DEFAULT 0,
        BuySignals INT NOT NULL DEFAULT 0,
        SellSignals INT NOT NULL DEFAULT 0,
        NoTradeSignals INT NOT NULL DEFAULT 0,

        -- Binary results
        BinaryTrades INT NOT NULL DEFAULT 0,
        BinaryWins INT NOT NULL DEFAULT 0,
        BinaryLosses INT NOT NULL DEFAULT 0,
        BinaryWinRate DECIMAL(5,2) NULL,
        BinaryTotalPnL DECIMAL(18,2) NOT NULL DEFAULT 0,

        -- CFD results
        CFDTrades INT NOT NULL DEFAULT 0,
        CFDWins INT NOT NULL DEFAULT 0,
        CFDLosses INT NOT NULL DEFAULT 0,
        CFDWinRate DECIMAL(5,2) NULL,
        CFDTotalPnL DECIMAL(18,2) NOT NULL DEFAULT 0,

        -- Risk metrics
        MaxDrawdown DECIMAL(18,2) NULL,
        MaxConsecutiveLosses INT NULL,
        SharpeRatio DECIMAL(8,4) NULL,

        -- Parameters used
        BinaryStakeUSD DECIMAL(18,2) NOT NULL,
        CFDVolume DECIMAL(18,4) NOT NULL,
        CFDStopLossPercent DECIMAL(5,2) NOT NULL,
        CFDTakeProfitPercent DECIMAL(5,2) NOT NULL,

        -- Timestamps
        CreatedAt DATETIME NOT NULL DEFAULT GETUTCDATE(),
        CompletedAt DATETIME NULL,

        -- Notes
        Notes NVARCHAR(1000) NULL,

        INDEX IX_BacktestRuns_StrategyName (StrategyName),
        INDEX IX_BacktestRuns_CreatedAt (CreatedAt)
    );

    PRINT 'BacktestRuns table created successfully';
END
GO

-- ===== BACKTEST TRADES TABLE =====
-- Stores individual trade results from backtests
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'BacktestTrades')
BEGIN
    CREATE TABLE BacktestTrades (
        TradeId BIGINT IDENTITY(1,1) PRIMARY KEY,
        RunId INT NOT NULL,
        TradeDate DATE NOT NULL,

        -- Signal data (same as DowOpenGSSignals)
        GS_PreviousClose DECIMAL(18,4) NOT NULL,
        GS_LatestPrice DECIMAL(18,4) NOT NULL,
        GS_Direction NVARCHAR(10) NOT NULL,
        GS_Change DECIMAL(18,4) NOT NULL,

        YM_PreviousClose DECIMAL(18,4) NOT NULL,
        YM_LatestPrice DECIMAL(18,4) NOT NULL,
        YM_Direction NVARCHAR(10) NOT NULL,
        YM_Change DECIMAL(18,4) NOT NULL,

        FinalSignal NVARCHAR(10) NOT NULL,
        NoTradeReason NVARCHAR(200) NULL,

        -- Binary trade result
        BinaryExpiry INT NULL,
        BinaryEntryPrice DECIMAL(18,4) NULL,
        BinaryExitPrice DECIMAL(18,4) NULL,
        BinaryResult NVARCHAR(10) NULL,  -- WIN, LOSS, NULL (no trade)
        BinaryPnL DECIMAL(18,2) NULL,

        -- CFD trade result
        CFDEntryPrice DECIMAL(18,4) NULL,
        CFDExitPrice DECIMAL(18,4) NULL,
        CFDStopLoss DECIMAL(18,4) NULL,
        CFDTakeProfit DECIMAL(18,4) NULL,
        CFDExitReason NVARCHAR(20) NULL,  -- TP_HIT, SL_HIT, TIME_EXIT
        CFDResult NVARCHAR(10) NULL,  -- WIN, LOSS, NULL (no trade)
        CFDPnL DECIMAL(18,2) NULL,
        CFDExitTimeUtc DATETIME NULL,

        -- Timestamps
        SnapshotTimeUtc DATETIME NOT NULL,
        EntryTimeUtc DATETIME NOT NULL,

        CONSTRAINT FK_BacktestTrades_RunId FOREIGN KEY (RunId) REFERENCES BacktestRuns(RunId),

        INDEX IX_BacktestTrades_RunId (RunId),
        INDEX IX_BacktestTrades_TradeDate (TradeDate),
        INDEX IX_BacktestTrades_FinalSignal (FinalSignal)
    );

    PRINT 'BacktestTrades table created successfully';
END
GO

-- ===== VERIFY CREATION =====
SELECT 'MarketPriceHistory' AS TableName, COUNT(*) AS RecordCount FROM MarketPriceHistory;
SELECT 'BacktestRuns' AS TableName, COUNT(*) AS RecordCount FROM BacktestRuns;
SELECT 'BacktestTrades' AS TableName, COUNT(*) AS RecordCount FROM BacktestTrades;
GO

PRINT 'Backtest tables created successfully!';
PRINT 'Next steps:';
PRINT '1. Import historical 1-minute data for GS, YM=F, and WS30 into MarketPriceHistory';
PRINT '2. Run backtest with: dotnet run -- --mode=backtest --from=2024-01-01 --to=2024-06-30';
GO

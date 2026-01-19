-- Binary Options Backtesting Tables
-- Used for backtesting scheduled signal providers like CMFLIX

-- Store 5-minute (or other interval) candles for backtesting
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('BacktestCandles') AND type = 'U')
BEGIN
    CREATE TABLE BacktestCandles (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        Symbol NVARCHAR(20) NOT NULL,
        TimeUtc DATETIME NOT NULL,
        [Open] DECIMAL(18,8) NOT NULL,
        High DECIMAL(18,8) NOT NULL,
        Low DECIMAL(18,8) NOT NULL,
        [Close] DECIMAL(18,8) NOT NULL,
        DataSource NVARCHAR(20) NOT NULL DEFAULT 'Deriv',
        CreatedAtUtc DATETIME NOT NULL DEFAULT GETUTCDATE()
    );

    CREATE INDEX IX_BacktestCandles_SymbolTime ON BacktestCandles(Symbol, TimeUtc);
    CREATE INDEX IX_BacktestCandles_TimeUtc ON BacktestCandles(TimeUtc);

    PRINT 'Created BacktestCandles table';
END
GO

-- Track backtest runs for any binary provider
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('BinaryBacktestRuns') AND type = 'U')
BEGIN
    CREATE TABLE BinaryBacktestRuns (
        RunId INT IDENTITY(1,1) PRIMARY KEY,
        ProviderName NVARCHAR(50) NOT NULL,
        StartDate DATE NOT NULL,
        EndDate DATE NOT NULL,
        TotalSignals INT NULL,
        Wins INT NULL,
        Losses INT NULL,
        WinRate DECIMAL(5,2) NULL,
        TotalPnL DECIMAL(18,4) NULL,
        StakePerTrade DECIMAL(18,2) NULL,
        ExpiryMinutes INT NULL,
        Notes NVARCHAR(500) NULL,
        CreatedAtUtc DATETIME NOT NULL DEFAULT GETUTCDATE(),
        CompletedAtUtc DATETIME NULL
    );

    CREATE INDEX IX_BinaryBacktestRuns_ProviderName ON BinaryBacktestRuns(ProviderName);
    CREATE INDEX IX_BinaryBacktestRuns_CreatedAt ON BinaryBacktestRuns(CreatedAtUtc);

    PRINT 'Created BinaryBacktestRuns table';
END
GO

-- Store individual backtest trade results
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('BinaryBacktestTrades') AND type = 'U')
BEGIN
    CREATE TABLE BinaryBacktestTrades (
        TradeId BIGINT IDENTITY(1,1) PRIMARY KEY,
        RunId INT NOT NULL FOREIGN KEY REFERENCES BinaryBacktestRuns(RunId),
        Symbol NVARCHAR(20) NOT NULL,
        Direction NVARCHAR(10) NOT NULL,  -- CALL/PUT
        ScheduledAtUtc DATETIME NOT NULL,
        EntryPrice DECIMAL(18,8) NULL,
        ExitPrice DECIMAL(18,8) NULL,
        ExpiryMinutes INT NOT NULL,
        Result NVARCHAR(10) NULL,  -- WIN/LOSS/NO_DATA
        PnL DECIMAL(18,4) NULL,
        Notes NVARCHAR(200) NULL
    );

    CREATE INDEX IX_BinaryBacktestTrades_RunId ON BinaryBacktestTrades(RunId);
    CREATE INDEX IX_BinaryBacktestTrades_Symbol ON BinaryBacktestTrades(Symbol);

    PRINT 'Created BinaryBacktestTrades table';
END
GO

PRINT 'Binary backtest tables setup complete';

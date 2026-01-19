-- Migration: Create SymbolInfo table for cTrader symbol configuration
-- This table stores symbol metadata needed for correct lot sizing and pip calculations

CREATE TABLE SymbolInfo (
    SymbolInfoId INT IDENTITY(1,1) PRIMARY KEY,
    CTraderSymbolId BIGINT NOT NULL,
    SymbolName NVARCHAR(100) NOT NULL,
    BaseAsset NVARCHAR(20) NULL,
    QuoteAsset NVARCHAR(20) NULL,

    -- Pip/Price configuration
    PipPosition INT NOT NULL DEFAULT 5,           -- Decimal places (3 for synthetics, 5 for forex)
    MinChange DECIMAL(18,8) NOT NULL DEFAULT 0.00001,

    -- Volume/Lot configuration
    LotSize DECIMAL(18,8) NOT NULL DEFAULT 100000, -- 1 for synthetics, 100000 for forex
    MinTradeQuantity DECIMAL(18,8) NOT NULL DEFAULT 0.01,
    MaxTradeQuantity DECIMAL(18,8) NOT NULL DEFAULT 100,
    StepVolume DECIMAL(18,8) NOT NULL DEFAULT 0.01,

    -- SL/TP constraints
    MinSlDistancePips INT NOT NULL DEFAULT 0,
    MinTpDistancePips INT NOT NULL DEFAULT 0,

    -- Costs
    Commission DECIMAL(18,8) NULL,
    SwapLong DECIMAL(18,8) NULL,
    SwapShort DECIMAL(18,8) NULL,

    -- Metadata
    IsEnabled BIT NOT NULL DEFAULT 1,
    Category NVARCHAR(50) NULL,  -- 'Synthetic', 'Forex', 'Crypto', 'Metals', etc.
    LastUpdatedUtc DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CreatedAtUtc DATETIME2 NOT NULL DEFAULT GETUTCDATE(),

    -- Indexes
    CONSTRAINT UQ_SymbolInfo_CTraderSymbolId UNIQUE (CTraderSymbolId),
    INDEX IX_SymbolInfo_SymbolName (SymbolName),
    INDEX IX_SymbolInfo_Category (Category)
);

-- Insert known synthetic symbols (from your cTrader screenshot)
-- These can be updated dynamically later via API

INSERT INTO SymbolInfo (CTraderSymbolId, SymbolName, BaseAsset, QuoteAsset, PipPosition, MinChange,
                        LotSize, MinTradeQuantity, MaxTradeQuantity, StepVolume,
                        MinSlDistancePips, MinTpDistancePips, Category)
VALUES
    -- Volatility 25 Index (from your screenshot)
    (2430, 'Volatility 25', 'R_25', 'USD', 3, 0.001,
     1, 0.50, 330.00, 0.01,
     423, 423, 'Synthetic'),

    -- Other common synthetics (adjust CTraderSymbolId as needed)
    (0, 'Volatility 10', 'R_10', 'USD', 3, 0.001,
     1, 0.30, 500.00, 0.01,
     100, 100, 'Synthetic'),

    (0, 'Volatility 50', 'R_50', 'USD', 4, 0.0001,
     1, 0.50, 200.00, 0.01,
     200, 200, 'Synthetic'),

    (0, 'Volatility 75', 'R_75', 'USD', 4, 0.0001,
     1, 0.50, 150.00, 0.01,
     250, 250, 'Synthetic'),

    (0, 'Volatility 100', 'R_100', 'USD', 4, 0.0001,
     1, 0.30, 100.00, 0.01,
     300, 300, 'Synthetic');

-- Sample forex symbols for reference
INSERT INTO SymbolInfo (CTraderSymbolId, SymbolName, BaseAsset, QuoteAsset, PipPosition, MinChange,
                        LotSize, MinTradeQuantity, MaxTradeQuantity, StepVolume,
                        MinSlDistancePips, MinTpDistancePips, Category)
VALUES
    (0, 'EURUSD', 'EUR', 'USD', 5, 0.00001,
     100000, 0.01, 100.00, 0.01,
     10, 10, 'Forex'),

    (0, 'GBPUSD', 'GBP', 'USD', 5, 0.00001,
     100000, 0.01, 100.00, 0.01,
     10, 10, 'Forex'),

    (0, 'XAUUSD', 'XAU', 'USD', 2, 0.01,
     100, 0.01, 50.00, 0.01,
     50, 50, 'Metals');

GO

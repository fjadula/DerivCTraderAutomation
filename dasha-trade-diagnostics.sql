-- =============================================================
-- Dasha Trade Diagnostics Query
-- Run this to check the status of Dasha Trade signals & trades
-- =============================================================

PRINT '=== DASHA PROVIDER CONFIGURATION ===';
SELECT
    ProviderChannelId,
    ProviderName,
    InitialStake,
    LadderSteps,
    IsActive,
    ExecuteOnProviderLoss,
    DefaultExpiryMinutes
FROM DashaProviderConfig;

PRINT '';
PRINT '=== DASHA COMPOUNDING STATE ===';
SELECT
    ProviderChannelId,
    CurrentStep,
    CurrentStake,
    ConsecutiveWins,
    TotalWins,
    TotalLosses,
    TotalProfit,
    LastTradeAt,
    UpdatedAt
FROM DashaCompoundingState;

PRINT '';
PRINT '=== DASHA PENDING SIGNALS (Last 20) ===';
SELECT TOP 20
    PendingSignalId,
    Asset,
    Direction,
    Timeframe,
    ExpiryMinutes,
    EntryPrice,
    ExitPrice,
    Status,
    ProviderResult,
    SignalReceivedAt,
    ExpiryAt,
    EvaluatedAt,
    DATEDIFF(SECOND, SignalReceivedAt, ISNULL(EvaluatedAt, GETUTCDATE())) AS AgeSeconds
FROM DashaPendingSignals
ORDER BY SignalReceivedAt DESC;

PRINT '';
PRINT '=== DASHA PENDING SIGNALS BY STATUS ===';
SELECT
    Status,
    COUNT(*) AS Count
FROM DashaPendingSignals
GROUP BY Status
ORDER BY Count DESC;

PRINT '';
PRINT '=== DASHA SIGNALS NEEDING ENTRY PRICE ===';
SELECT
    PendingSignalId,
    Asset,
    Direction,
    Status,
    EntryPrice,
    SignalReceivedAt,
    DATEDIFF(SECOND, SignalReceivedAt, GETUTCDATE()) AS AgeSeconds
FROM DashaPendingSignals
WHERE Status = 'AwaitingExpiry' AND EntryPrice = 0
ORDER BY SignalReceivedAt DESC;

PRINT '';
PRINT '=== DASHA SIGNALS AWAITING EVALUATION (Expired) ===';
SELECT
    PendingSignalId,
    Asset,
    Direction,
    EntryPrice,
    Status,
    SignalReceivedAt,
    ExpiryAt,
    DATEDIFF(SECOND, ExpiryAt, GETUTCDATE()) AS SecondsPastExpiry
FROM DashaPendingSignals
WHERE Status = 'AwaitingExpiry'
  AND ExpiryAt <= GETUTCDATE()
  AND EntryPrice > 0
ORDER BY ExpiryAt ASC;

PRINT '';
PRINT '=== DASHA TRADES (Last 20) ===';
SELECT TOP 20
    TradeId,
    Asset,
    Direction,
    Stake,
    StakeStep,
    DerivContractId,
    ExecutionResult,
    Profit,
    ProviderResult,
    ExecutedAt,
    SettledAt
FROM DashaTrades
ORDER BY CreatedAt DESC;

PRINT '';
PRINT '=== DASHA TRADES BY RESULT ===';
SELECT
    ExecutionResult,
    COUNT(*) AS Count,
    SUM(Profit) AS TotalProfit,
    AVG(Stake) AS AvgStake
FROM DashaTrades
WHERE ExecutionResult IS NOT NULL
GROUP BY ExecutionResult
ORDER BY Count DESC;

PRINT '';
PRINT '=== DASHA UNSETTLED TRADES ===';
SELECT
    TradeId,
    Asset,
    Direction,
    Stake,
    DerivContractId,
    ExecutedAt,
    ExpiryMinutes,
    DATEDIFF(SECOND, ExecutedAt, GETUTCDATE()) AS AgeSeconds
FROM DashaTrades
WHERE SettledAt IS NULL AND DerivContractId IS NOT NULL
ORDER BY ExecutedAt DESC;

PRINT '';
PRINT '=== SUMMARY ===';
SELECT
    'Total Signals Received' AS Metric,
    COUNT(*) AS Value
FROM DashaPendingSignals
UNION ALL
SELECT
    'Signals Awaiting Entry Price',
    COUNT(*)
FROM DashaPendingSignals
WHERE Status = 'AwaitingExpiry' AND EntryPrice = 0
UNION ALL
SELECT
    'Signals Ready for Evaluation',
    COUNT(*)
FROM DashaPendingSignals
WHERE Status = 'AwaitingExpiry' AND ExpiryAt <= GETUTCDATE() AND EntryPrice > 0
UNION ALL
SELECT
    'Provider Won (No Trade)',
    COUNT(*)
FROM DashaPendingSignals
WHERE Status = 'ProviderWon'
UNION ALL
SELECT
    'Provider Lost (Trade Executed)',
    COUNT(*)
FROM DashaPendingSignals
WHERE Status = 'ProviderLost'
UNION ALL
SELECT
    'Total Trades Executed',
    COUNT(*)
FROM DashaTrades
UNION ALL
SELECT
    'Unsettled Trades',
    COUNT(*)
FROM DashaTrades
WHERE SettledAt IS NULL AND DerivContractId IS NOT NULL;

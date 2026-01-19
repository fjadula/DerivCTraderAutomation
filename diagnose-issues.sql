-- Diagnose TP and Processed issues
USE khulafx;

-- Check the most recent signal that was created
SELECT TOP 5
    SignalId,
    Asset,
    Direction,
    EntryPrice,
    StopLoss,
    TakeProfit,
    TakeProfit2,
    TakeProfit3,
    TakeProfit4,
    Processed,
    ProcessedAt,
    ReceivedAt,
    RawMessage
FROM ParsedSignalsQueue
ORDER BY ReceivedAt DESC;

-- Count how many orders were created for each signal
SELECT
    pq.SignalId,
    pq.Asset,
    pq.Direction,
    pq.Processed,
    COUNT(*) as SignalCount,
    pq.ReceivedAt
FROM ParsedSignalsQueue pq
WHERE pq.ReceivedAt > DATEADD(MINUTE, -30, GETUTCDATE())
GROUP BY pq.SignalId, pq.Asset, pq.Direction, pq.Processed, pq.ReceivedAt
HAVING COUNT(*) > 1
ORDER BY pq.ReceivedAt DESC;

-- Show unprocessed signals
SELECT
    SignalId,
    Asset,
    Direction,
    EntryPrice,
    StopLoss,
    TakeProfit,
    Processed,
    DATEDIFF(SECOND, ReceivedAt, GETUTCDATE()) AS SecondsOld
FROM ParsedSignalsQueue
WHERE Processed = 0
ORDER BY ReceivedAt DESC;

-- Check if signals are being received and parsed
USE khulafx;

-- Check recent signals in ParsedSignalsQueue
SELECT TOP 10
    SignalId,
    Asset,
    Direction,
    EntryPrice,
    StopLoss,
    TakeProfit,
    ProviderName,
    ProviderChannelId,
    Processed,
    ReceivedAt,
    ProcessedAt,
    DATEDIFF(MINUTE, ReceivedAt, GETUTCDATE()) AS MinutesAgo
FROM ParsedSignalsQueue
ORDER BY ReceivedAt DESC;

-- Count by processed status
SELECT
    Processed,
    COUNT(*) AS Count
FROM ParsedSignalsQueue
GROUP BY Processed;

-- Check if there are any recent signals at all
SELECT
    COUNT(*) AS TotalSignals,
    MAX(ReceivedAt) AS LatestSignalTime,
    DATEDIFF(MINUTE, MAX(ReceivedAt), GETUTCDATE()) AS MinutesSinceLastSignal
FROM ParsedSignalsQueue;

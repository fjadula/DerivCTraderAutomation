-- Check if DashaTrade signals are being saved to ParsedSignalsQueue (wrong table)
-- This would indicate the routing logic in TelegramSignalScraperService isn't working

PRINT '=== DASHATRADE SIGNALS IN PARSEDSIGNALSQUEUE (Last 20) ===';
SELECT TOP 20
    SignalId,
    Asset,
    Direction,
    EntryPrice,
    TakeProfit,
    StopLoss,
    ProviderName,
    ProviderChannelId,
    SignalType,
    Processed,
    ReceivedAt,
    RawMessage
FROM ParsedSignalsQueue
WHERE ProviderChannelId = '-1001570351142'  -- DashaTrade channel
ORDER BY ReceivedAt DESC;

PRINT '';
PRINT '=== DASHATRADE SIGNALS COUNT BY STATUS ===';
SELECT
    Processed,
    COUNT(*) AS Count
FROM ParsedSignalsQueue
WHERE ProviderChannelId = '-1001570351142'
GROUP BY Processed;

PRINT '';
PRINT '=== ALL SIGNALS FROM LAST 24 HOURS (All Providers) ===';
SELECT
    ProviderChannelId,
    ProviderName,
    COUNT(*) AS SignalCount
FROM ParsedSignalsQueue
WHERE ReceivedAt >= DATEADD(HOUR, -24, GETUTCDATE())
GROUP BY ProviderChannelId, ProviderName
ORDER BY SignalCount DESC;

PRINT '';
PRINT '=== CHECK IF DASHATRADE PROVIDER IS CONFIGURED ===';
SELECT
    ProviderChannelId,
    ProviderName,
    TakeOriginal,
    TakeOpposite,
    IsActive
FROM ProviderChannelConfig
WHERE ProviderChannelId = '-1001570351142'
   OR ProviderName LIKE '%Dasha%';

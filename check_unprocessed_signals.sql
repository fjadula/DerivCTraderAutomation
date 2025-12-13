-- Check for unprocessed signals in the database
-- Run this to see if there are any signals that need to be marked as processed

USE khulafx;

-- Count unprocessed signals
SELECT
    COUNT(*) AS TotalUnprocessedSignals
FROM ParsedSignalsQueue
WHERE Processed = 0;

-- Show details of unprocessed signals
SELECT
    SignalId,
    Asset,
    Direction,
    EntryPrice,
    StopLoss,
    TakeProfit,
    ProviderName,
    ProviderChannelId,
    ReceivedAt,
    DATEDIFF(MINUTE, ReceivedAt, GETUTCDATE()) AS MinutesAgo
FROM ParsedSignalsQueue
WHERE Processed = 0
ORDER BY ReceivedAt DESC;

-- OPTIONAL: Mark old unprocessed signals as processed to prevent duplicate orders
-- Uncomment the following lines if you want to mark signals older than 1 hour as processed

/*
UPDATE ParsedSignalsQueue
SET Processed = 1, ProcessedAt = GETUTCDATE()
WHERE Processed = 0
  AND DATEDIFF(MINUTE, ReceivedAt, GETUTCDATE()) > 60;

SELECT @@ROWCOUNT AS SignalsMarkedAsProcessed;
*/

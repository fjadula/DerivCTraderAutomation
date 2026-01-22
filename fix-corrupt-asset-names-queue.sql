-- =============================================
-- Fix Corrupt Asset Names in TradeExecutionQueue
-- Issue: Asset column contains direction appended
-- Examples: "CRASH 500 Buy", "Volatility 30 1s Sell"
-- Should be: "CRASH 500", "Volatility 30 1s"
-- =============================================

PRINT '=== CHECKING FOR CORRUPT ASSET NAMES IN QUEUE ===';
SELECT
    QueueId,
    Asset,
    Direction,
    StrategyName,
    CreatedAt,
    Processed
FROM TradeExecutionQueue
WHERE Asset LIKE '% Buy'
   OR Asset LIKE '% Sell'
ORDER BY CreatedAt DESC;

PRINT '';
PRINT '=== CORRUPT QUEUE ASSETS COUNT ===';
SELECT
    COUNT(*) AS CorruptAssetCount
FROM TradeExecutionQueue
WHERE Asset LIKE '% Buy'
   OR Asset LIKE '% Sell';

PRINT '';
PRINT '=== FIX CORRUPT ASSET NAMES IN QUEUE ===';
PRINT 'Removing " Buy" and " Sell" suffixes from Asset column...';

-- Fix "Buy" suffix
UPDATE TradeExecutionQueue
SET Asset = LEFT(Asset, LEN(Asset) - 4)
WHERE Asset LIKE '% Buy';

PRINT 'Fixed queue assets ending with " Buy"';

-- Fix "Sell" suffix
UPDATE TradeExecutionQueue
SET Asset = LEFT(Asset, LEN(Asset) - 5)
WHERE Asset LIKE '% Sell';

PRINT 'Fixed queue assets ending with " Sell"';

PRINT '';
PRINT '=== VERIFICATION - Should show 0 corrupt assets ===';
SELECT
    COUNT(*) AS RemainingCorruptAssets
FROM TradeExecutionQueue
WHERE Asset LIKE '% Buy'
   OR Asset LIKE '% Sell';

PRINT '';
PRINT '=== SAMPLE OF FIXED QUEUE ASSETS ===';
SELECT TOP 20
    QueueId,
    Asset,
    Direction,
    StrategyName,
    CreatedAt,
    Processed
FROM TradeExecutionQueue
ORDER BY CreatedAt DESC;

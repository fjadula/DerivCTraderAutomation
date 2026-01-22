-- =============================================
-- Fix Corrupt Asset Names in ParsedSignalsQueue
-- Issue: Asset column contains direction appended
-- Examples: "CRASH 500 Buy", "Volatility 30 1s Buy"
-- Should be: "CRASH 500", "Volatility 30 1s"
-- =============================================

PRINT '=== CHECKING FOR CORRUPT ASSET NAMES ===';
SELECT
    SignalId,
    Asset,
    Direction,
    ProviderName,
    ReceivedAt,
    Processed
FROM ParsedSignalsQueue
WHERE Asset LIKE '% Buy'
   OR Asset LIKE '% Sell'
ORDER BY ReceivedAt DESC;

PRINT '';
PRINT '=== CORRUPT ASSETS COUNT ===';
SELECT
    COUNT(*) AS CorruptAssetCount
FROM ParsedSignalsQueue
WHERE Asset LIKE '% Buy'
   OR Asset LIKE '% Sell';

PRINT '';
PRINT '=== FIX CORRUPT ASSET NAMES ===';
PRINT 'Removing " Buy" and " Sell" suffixes from Asset column...';

-- Fix "Buy" suffix
UPDATE ParsedSignalsQueue
SET Asset = LEFT(Asset, LEN(Asset) - 4)
WHERE Asset LIKE '% Buy';

PRINT 'Fixed assets ending with " Buy"';

-- Fix "Sell" suffix
UPDATE ParsedSignalsQueue
SET Asset = LEFT(Asset, LEN(Asset) - 5)
WHERE Asset LIKE '% Sell';

PRINT 'Fixed assets ending with " Sell"';

PRINT '';
PRINT '=== VERIFICATION - Should show 0 corrupt assets ===';
SELECT
    COUNT(*) AS RemainingCorruptAssets
FROM ParsedSignalsQueue
WHERE Asset LIKE '% Buy'
   OR Asset LIKE '% Sell';

PRINT '';
PRINT '=== SAMPLE OF FIXED ASSETS ===';
SELECT TOP 20
    SignalId,
    Asset,
    Direction,
    ProviderName,
    ReceivedAt,
    Processed
FROM ParsedSignalsQueue
WHERE ProviderName IN ('SyntheticIndicesTrader', 'VIP KNIGHTS')
ORDER BY ReceivedAt DESC;

-- IzintzikaDeriv Provider Configuration
-- Channel ID: -1001666286465 (IzintzikaDeriv)
-- Signal type: Forex entry orders (placed on cTrader, then executed on Deriv binary upon fill)
-- Format: Multiple signals per message with 📊{ASSET} {PUT|CALL} {ENTRY_PRICE}

-- Insert provider configuration
-- TakeOriginal=1: Place orders in the direction specified (PUT=Sell, CALL=Buy)
-- TakeOpposite=0: Don't place opposite direction orders (change to 1 if desired)
INSERT INTO ProviderChannelConfig (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite, IsActive, CreatedAt)
VALUES ('-1001666286465', 'IzintzikaDeriv', 1, 0, 1, GETUTCDATE());

-- Verify insertion
SELECT * FROM ProviderChannelConfig WHERE ProviderChannelId = '-1001666286465';

GO

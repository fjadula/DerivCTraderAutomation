-- DerivPlus Provider Configuration
-- Channel ID: -1628868943 (DerivPlus)
-- Signal type: PureBinary (straight to Deriv binary)
-- Expiry: Per-signal (parsed from "Expiry: N Minutes" and stored as Timeframe like "NM")

-- Insert provider configuration (adjust TakeOriginal/TakeOpposite as desired)
INSERT INTO ProviderChannelConfig (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite, IsActive, CreatedAt)
VALUES ('-1628868943', 'DerivPlus', 1, 0, 1, GETUTCDATE());

-- Verify insertion
SELECT * FROM ProviderChannelConfig WHERE ProviderChannelId = '-1628868943';

GO

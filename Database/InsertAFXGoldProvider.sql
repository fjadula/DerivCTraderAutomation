-- AFXGold Provider Configuration
-- Channel ID: -1003367695960
-- Binary Expiry: 40 minutes (default for this provider)

-- Insert provider configuration
INSERT INTO ProviderChannelConfig (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite, IsActive, CreatedAt)
VALUES ('-1003367695960', 'AFXGold', 1, 0, 1, GETUTCDATE());

-- Verify insertion
SELECT * FROM ProviderChannelConfig WHERE ProviderChannelId = '-1003367695960';

GO

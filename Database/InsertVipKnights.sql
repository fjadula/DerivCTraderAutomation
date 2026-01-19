-- VIP KNIGHTS Provider Configuration
-- Run this script to add/update VIP KNIGHTS channel

-- Check if exists and update, otherwise insert
IF EXISTS (SELECT 1 FROM ProviderChannelConfig WHERE ProviderChannelId = '-1003046812685')
BEGIN
    UPDATE ProviderChannelConfig
    SET ProviderName = 'VIP KNIGHTS',
        TakeOriginal = 1,
        TakeOpposite = 0,
        IsActive = 1
    WHERE ProviderChannelId = '-1003046812685';
    PRINT 'Updated VIP KNIGHTS provider configuration';
END
ELSE
BEGIN
    INSERT INTO ProviderChannelConfig (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite, IsActive, CreatedAt)
    VALUES ('-1003046812685', 'VIP KNIGHTS', 1, 0, 1, GETUTCDATE());
    PRINT 'Inserted VIP KNIGHTS provider configuration';
END

-- Verify
SELECT * FROM ProviderChannelConfig WHERE ProviderChannelId = '-1003046812685';
GO

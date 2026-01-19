-- DerivGold Provider Configuration
-- Run this script to configure all 8 Gold/synthetic signal channels

-- Insert provider configurations
INSERT INTO ProviderChannelConfig (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite, IsActive, CreatedAt)
VALUES
    -- Gold Signal 1
    ('-1001357835235', 'DerivGold', 1, 0, 1, GETUTCDATE()),
    
    -- Mega Spikes Max (Boom/Crash - NO DERIV BINARY)
    ('-1001060006944', 'DerivGold', 1, 0, 1, GETUTCDATE()),
    
    -- Volatility Signals
    ('-1001768939027', 'DerivGold', 1, 0, 1, GETUTCDATE()),
    
    -- Gold Signal 2
    ('-1002782055957', 'DerivGold', 1, 0, 1, GETUTCDATE()),
    
    -- Gold Signal 3
    ('-1001685029638', 'DerivGold', 1, 0, 1, GETUTCDATE()),
    
    -- Gold/US30 Signals
    ('-1001631556618', 'DerivGold', 1, 0, 1, GETUTCDATE()),
    
    -- Gold Signal 4
    ('-1002242743399', 'DerivGold', 1, 0, 1, GETUTCDATE()),
    
    -- VIX Signals
    ('-1003046812685', 'DerivGold', 1, 0, 1, GETUTCDATE());

-- Verify insertion
SELECT * FROM ProviderChannelConfig WHERE ProviderName = 'DerivGold' ORDER BY ProviderChannelId;

-- Optional: Set default expiry times for different asset types
-- (This assumes you have an ExpiryConfig table - adjust as needed)

-- Gold/US30: 30 minutes
-- Volatility: 15 minutes
-- Boom/Crash: N/A (no binary)
-- VIX: 15 minutes

GO

# cTrader Forex Signal Processing Implementation

## Summary

Implemented the missing `CTraderForexProcessorService` to process forex signals from `ParsedSignalsQueue` and create pending orders on cTrader. This completes the forex signal flow that was previously only handling pure binary signals.

## What Was Missing

Previously, the `BinaryExecutionService` was processing **ALL** signals from `ParsedSignalsQueue` and executing them as direct binary options on Deriv, which was incorrect for forex signals that need to go through cTrader first.

## What Was Implemented

### 1. New Service: `CTraderForexProcessorService`

**Location**: `src/DerivCTrader.TradeExecutor/Services/CTraderForexProcessorService.cs`

**Purpose**: 
- Polls `ParsedSignalsQueue` for unprocessed forex signals (excluding pure binary)
- Loads provider configuration (TakeOriginal/TakeOpposite)
- Creates pending orders on cTrader via `ICTraderPendingOrderService`
- Marks signals as processed after order creation

**Key Features**:
- Configurable poll interval (default: 5 seconds)
- Respects provider TakeOriginal/TakeOpposite flags
- Creates 1 or 2 pending orders per signal based on configuration
- Comprehensive logging and console output

### 2. Updated: `BinaryExecutionService`

**Changes**:
- Now **only** processes signals where `SignalType == SignalType.PureBinary`
- Forex signals are ignored (handled by `CTraderForexProcessorService`)
- Added using statement for `DerivCTrader.Domain.Enums`

### 3. Updated: `Program.cs` (TradeExecutor)

**Changes**:
- Added `using DerivCTrader.Infrastructure.CTrader.Extensions`
- Registered cTrader services: `services.AddCTraderServices(configuration)`
- Registered `CTraderForexProcessorService` as a hosted service
- Updated console output to show all registered services

### 4. Updated: `appsettings.Production.json`

**Changes**:
- Added `WebSocketUrl` to cTrader configuration
- Added `SignalPollIntervalSeconds: 5` for forex processor polling

## Complete Flow: Forex Signal → cTrader → Deriv Binary → TradeExecutionQueue

### Step 1: Signal Scraping (SignalScraper)
```
1. Telegram message received
2. Parser extracts signal (NewStrats, VipFx, etc.)
3. Save to ParsedSignalsQueue (Processed = false)
```

### Step 2: Forex Processing (TradeExecutor - NEW)
```
4. CTraderForexProcessorService polls ParsedSignalsQueue
5. Filters for forex signals (SignalType != PureBinary)
6. Loads ProviderChannelConfig for channel
7. If TakeOriginal = true:
   - Create pending BuyLimit/SellLimit order at entry price
8. If TakeOpposite = true:
   - Create opposite pending order
9. Mark signal as Processed
```

### Step 3: cTrader Price Monitor (Existing)
```
10. CTraderPriceMonitor watches tick stream
11. Detects when price crosses entry in CORRECT direction
12. Pending order converts to market execution
13. OrderExecuted event fires
```

### Step 4: Write to TradeExecutionQueue (Existing)
```
14. CTraderPendingOrderService.OnOrderCrossed handles event
15. Create TradeExecutionQueue entry:
    - CTraderOrderId
    - Asset
    - Direction
    - StrategyName (from ProviderName)
    - ProviderChannelId
    - IsOpposite flag
    - CreatedAt
16. Save to database
```

### Step 5: Deriv Binary Execution (Future - TODO)
```
17. DerivBinaryExecutorService polls TradeExecutionQueue
18. For each queue entry:
    - Calculate expiry (15min or 30min)
    - Execute binary option on Deriv
    - Get contract_id
19. (Optional) Match with KhulaFxTradeMonitor
```

## Database Tables Involved

### ParsedSignalsQueue
- **Purpose**: Store all parsed signals before processing
- **Key Columns**: SignalId, Asset, Direction, EntryPrice, ProviderChannelId, ProviderName, SignalType, Processed
- **Updated By**: TelegramSignalScraperService (INSERT), CTraderForexProcessorService (UPDATE Processed)

### ProviderChannelConfig
- **Purpose**: Provider settings and trade direction preferences
- **Key Columns**: ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite, IsActive
- **Read By**: CTraderForexProcessorService

### TradeExecutionQueue
- **Purpose**: Matching queue between cTrader executions and Deriv binaries
- **Key Columns**: QueueId, CTraderOrderId, Asset, Direction, StrategyName, ProviderChannelId, IsOpposite, CreatedAt
- **Written By**: CTraderPendingOrderService (after cTrader execution)
- **Read By**: DerivBinaryExecutorService (TODO)

## Configuration Requirements

### appsettings.Production.json (TradeExecutor)

```json
{
  "CTrader": {
    "ClientId": "...",
    "ClientSecret": "...",
    "AccessToken": "...",
    "Environment": "Demo",
    "DemoAccountId": "2295141",
    "DefaultLotSize": 0.2,
    "WebSocketUrl": "wss://demo.ctraderapi.com",
    "SignalPollIntervalSeconds": 5
  }
}
```

## Testing the Implementation

### 1. Start Both Services

```powershell
# Terminal 1: Signal Scraper
cd src\DerivCTrader.SignalScraper
dotnet run

# Terminal 2: Trade Executor
cd src\DerivCTrader.TradeExecutor
dotnet run
```

### 2. Send a Test Forex Signal

Send to NewStrats channel (`-1001628868943`):
```
OPEN EUR/CAD CALL Strat:1minbreakout
```

### 3. Expected Log Sequence

**SignalScraper**:
```
✅ PARSED SIGNAL:
   Asset: EUR/CAD
   Direction: Buy
💾 SAVED TO QUEUE: Signal #123
```

**TradeExecutor** (CTraderForexProcessorService):
```
📋 Processing 1 forex signal(s)...
🔨 Signal #123: EUR/CAD Buy @ 1.35000
   📝 Creating ORIGINAL order (Buy)...
   ✅ Original order placed
✅ Signal #123 complete - 1 pending order(s) on cTrader
```

**TradeExecutor** (CTraderPendingOrderService - when price crosses):
```
✅ Order executed: OrderId=98765
💾 Wrote to TradeExecutionQueue: QueueId=456
```

### 4. Verify Database

```sql
-- Check signal was processed
SELECT * FROM ParsedSignalsQueue 
WHERE SignalId = 123 AND Processed = 1;

-- Check TradeExecutionQueue entry created
SELECT * FROM TradeExecutionQueue 
WHERE Asset = 'EUR/CAD' 
ORDER BY CreatedAt DESC;
```

## Key Differences: Forex vs Pure Binary

| Aspect | Forex Signals | Pure Binary Signals |
|--------|--------------|---------------------|
| **Service** | `CTraderForexProcessorService` | `BinaryExecutionService` |
| **Flow** | ParsedSignalsQueue → cTrader → TradeExecutionQueue → Deriv | ParsedSignalsQueue → Deriv (direct) |
| **cTrader** | ✅ Creates pending orders | ❌ Skips cTrader |
| **SignalType** | `SignalType.Forex` or default | `SignalType.PureBinary` |
| **Providers** | NewStrats, VipFx, PerfectFx, etc. | VIP_CHANNEL only |
| **Processed By** | After pending order created | After Deriv execution |
| **Queue Entry** | Written after cTrader execution | Not written (direct execution) |

## Next Steps (TODO)

1. ✅ **DerivBinaryExecutorService**: COMPLETED - Processes TradeExecutionQueue and executes Deriv binaries
2. **cTrader Integration**: Complete WebSocket implementation for tick monitoring
3. **Error Handling**: Add retry logic for failed cTrader orders and Deriv executions
4. **Monitoring**: Add metrics for pending orders, execution rate, queue depth
5. **Testing**: Create integration tests for full forex flow

## Files Modified

1. ✅ `src/DerivCTrader.TradeExecutor/Services/CTraderForexProcessorService.cs` (CREATED)
2. ✅ `src/DerivCTrader.TradeExecutor/Services/DerivBinaryExecutorService.cs` (CREATED)
3. ✅ `src/DerivCTrader.TradeExecutor/Services/BinaryExecutionService.cs` (UPDATED)
4. ✅ `src/DerivCTrader.TradeExecutor/Program.cs` (UPDATED)
5. ✅ `src/DerivCTrader.TradeExecutor/appsettings.Production.json` (UPDATED)
6. ✅ `src/DerivCTrader.Infrastructure/Persistence/SqlServerTradeRepository.cs` (UPDATED)
7. ✅ `src/DerivCTrader.Infrastructure/CTrader/CTraderServiceExtensions.cs` (UPDATED)

## Build Status

✅ Build succeeded with no errors or warnings

---

**Date**: December 11, 2025  
**Status**: Ready for testing with live signals

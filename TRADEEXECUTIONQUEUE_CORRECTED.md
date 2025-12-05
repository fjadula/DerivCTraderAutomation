# TradeExecutionQueue Architecture - CORRECTED

## Critical Understanding

**TradeExecutionQueue is a MATCHING queue, NOT a full trade record!**

It sits between two trade detection systems:
- **cTrader** (writes when orders execute)
- **Deriv** (reads when binaries are detected)

---

## The Unified Flow

```
┌─────────────────────────────────────────────────────────────────┐
│ SIGNAL INGESTION (Telegram)                                    │
│ ParsedSignal → DB                                               │
└─────────────────────────────────────────────────────────────────┘
                           ↓
┌─────────────────────────────────────────────────────────────────┐
│ EXECUTION CHOICE (Platform-Dependent)                           │
└─────────────────────────────────────────────────────────────────┘
        ↓                                       ↓
   ┌─────────────────────┐            ┌────────────────────────┐
   │   PATH 1: cTRADER   │            │  PATH 2: DERIV-ONLY    │
   └─────────────────────┘            └────────────────────────┘
        ↓                                      ↓
   Place Pending Order              Place Binary Option
   at Entry Price                    at Stake + Expiry
        ↓                                      ↓
   Monitor Price Ticks              Return ContractId
   (ProtoOASpotEvent)               to Result
        ↓                                      ↓
   Price Crosses Entry         ┌─────────────────────────┐
   in Correct Direction        │ Write to TradeExecution │
        ↓                       │ Queue (for cTrader only)│
   ┌──────────────────────────┐└─────────────────────────┘
   │ Write to TradeExecution  │              ↓
   │ Queue (matching metadata)│    KhulaFxTradeMonitor
   │ - Asset                  │    detects binary
   │ - Direction              │    execution
   │ - StrategyName           │              ↓
   │ - ProviderChannelId      │    Match with Queue
   │ - CTraderOrderId         │    (Asset+Direction)
   │ - IsOpposite             │              ↓
   └──────────────────────────┘    Update BinaryTrades
        ↓                          with StrategyName
   Place Deriv Binary              ↓
   using Queue Metadata        Delete Queue Row
        ↓                          ↓
   Return ContractId          ✅ Cycle Complete
        ↓
   KhulaFxTradeMonitor
   detects binary
        ↓
   Match with Queue
   (Asset+Direction)
        ↓
   Update BinaryTrades
   with StrategyName
        ↓
   Delete Queue Row
        ↓
   ✅ Cycle Complete
```

---

## Database Tables

### TradeExecutionQueue (MATCHING QUEUE ONLY)
```sql
CREATE TABLE TradeExecutionQueue (
    QueueId INT IDENTITY(1,1) PRIMARY KEY,
    CTraderOrderId NVARCHAR(50),          -- From cTrader execution
    Asset NVARCHAR(20),                   -- e.g., "EURUSD"
    Direction NVARCHAR(10),               -- "Buy" or "Sell"
    StrategyName NVARCHAR(100),           -- "ProviderName_Asset_Timestamp"
    ProviderChannelId NVARCHAR(50),       -- For audit trail
    IsOpposite BIT,                       -- Opposite direction trade?
    CreatedAt DATETIME DEFAULT GETUTCDATE()
);
```

**Columns:**
- ✅ CTraderOrderId - From cTrader execution
- ✅ Asset - Matching key
- ✅ Direction - Matching key
- ✅ StrategyName - Metadata to copy to BinaryTrades
- ✅ ProviderChannelId - For audit/reporting
- ✅ IsOpposite - Trade reversal flag
- ✅ CreatedAt - Timestamp

**NOT in this table:**
- ❌ DerivContractId - This goes in BinaryOptionTrades
- ❌ Stake - This goes in BinaryOptionTrades
- ❌ ExpiryMinutes - This goes in BinaryOptionTrades
- ❌ Platform - Queue is only for cTrader→Deriv matching
- ❌ Outcome - This goes in BinaryOptionTrades
- ❌ Profit - This goes in BinaryOptionTrades

### BinaryOptionTrade (FULL DERIV RECORD)
```sql
CREATE TABLE BinaryOptionTrade (
    TradeId INT IDENTITY(1,1) PRIMARY KEY,
    AssetName NVARCHAR(20),
    Direction NVARCHAR(10),
    OpenTime DATETIME,
    CloseTime DATETIME,
    ExpiryLength INT,              -- Minutes
    Result NVARCHAR(50),           -- "Win", "Loss", etc.
    TradeStake DECIMAL(10,2),      -- Deriv stake
    ExpectedExpiryTimestamp DATETIME,
    StrategyName NVARCHAR(100),    -- Populated by KhulaFxTradeMonitor
    CreatedAt DATETIME,
    -- ... other columns
);
```

**Purpose:** Full record of binary option trade on Deriv
**Populated by:** TradeExecutor.BinaryExecutionService
**Updated by:** KhulaFxTradeMonitor (StrategyName, Result, CloseTime)

---

## Code Flow Implementation

### Step 1: cTrader Executes (CTraderPendingOrderService)
```csharp
// Order has executed, write to matching queue
var queueItem = new TradeExecutionQueue
{
    CTraderOrderId = orderId.ToString(),
    Asset = signal.Asset,
    Direction = signal.Direction.ToString(),
    StrategyName = $"{signal.ProviderName}_{signal.Asset}_{timestamp}",
    ProviderChannelId = signal.ProviderChannelId,
    IsOpposite = false,
    CreatedAt = DateTime.UtcNow
};

await _repository.EnqueueTradeAsync(queueItem);
```

**Result:** Queue row exists, waiting for Deriv placement

---

### Step 2: Deriv Executes (BinaryExecutionService)
```csharp
// Place binary option using queue metadata
var binaryTrade = new BinaryOptionTrade
{
    AssetName = signal.Asset,
    Direction = direction,
    OpenTime = DateTime.UtcNow,
    ExpiryLength = expiryMinutes,
    StrategyName = "initial",  // Will be updated by KhulaFxTradeMonitor
    TradeStake = _defaultStake,
    ExpectedExpiryTimestamp = DateTime.UtcNow.AddMinutes(expiryMinutes),
    CreatedAt = DateTime.UtcNow
};

await _repository.CreateBinaryTradeAsync(binaryTrade);
```

**Result:** BinaryOptionTrade row created with Deriv details

---

### Step 3: KhulaFxTradeMonitor Detects & Matches
```csharp
// Find matching queue entry by Asset+Direction (FIFO)
var queueEntry = await _repository.DequeueMatchingTradeAsync(
    asset: detectedAsset,
    direction: detectedDirection
);

if (queueEntry != null)
{
    // Update BinaryTrade with strategy name and metadata
    await _repository.UpdateBinaryTradeAsync(new BinaryOptionTrade
    {
        TradeId = detectedTradeId,
        StrategyName = queueEntry.StrategyName,  // Copy from queue
        // ... other updates
    });

    // Delete matched queue row
    await _repository.DeleteQueueItemAsync(queueEntry.QueueId);
}
```

**Result:** Queue row deleted, BinaryTrade enriched with matching data

---

## Two Execution Paths

### PATH 1: With cTrader (Full Flow)
```
Signal
  ↓
cTrader pending order
  ↓
Price cross detected
  ↓
Write to TradeExecutionQueue  ← MATCHING QUEUE
  ↓
Place Deriv binary
  ↓
KhulaFxTradeMonitor detects
  ↓
Match queue (Asset+Direction)  ← MATCH HERE
  ↓
Update BinaryTrades + Delete queue
  ↓
✅ Complete
```

### PATH 2: Deriv-Only (Direct)
```
Signal
  ↓
(Skip cTrader, no network)
  ↓
Place Deriv binary directly
  ↓
Write to BinaryOptionTrade  ← DIRECT, NO QUEUE
  ↓
KhulaFxTradeMonitor detects
  ↓
No queue entry, direct update
  ↓
✅ Complete
```

---

## Current Status

✅ **Corrected Implementation:**
- TradeExecutionQueue properly defined with matching metadata only
- CTraderPendingOrderService writes minimal correct schema
- BinaryExecutionService creates BinaryOptionTrade (full record)
- OutcomeMonitor uses correct repository methods

⏳ **Pending Network Resolution:**
- cTrader connectivity test needed
- If blocked: VPN, proxy, or cloud deployment required
- Until then: Deriv-only path works (skips cTrader completely)

---

## Key Takeaway

**TradeExecutionQueue is NOT a trade record—it's a matching key!**

| System | Write To | Read From | Purpose |
|--------|----------|-----------|---------|
| cTrader | TradeExecutionQueue | — | Write execution metadata |
| Deriv | BinaryOptionTrade | TradeExecutionQueue | Create trade, then match |
| KhulaFxTM | BinaryOptionTrade | TradeExecutionQueue | Update with strategy, delete queue |

Each table serves its purpose. Mixing concerns breaks the architecture.

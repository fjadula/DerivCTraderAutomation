# cTrader Open API Integration Guide

This document explains how the cTrader integration works in this system, highlighting key considerations for implementing a similar integration.

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Key Components](#key-components)
3. [Connection & Authentication](#connection--authentication)
4. [Order Flow](#order-flow)
5. [Order Types: LIMIT vs STOP](#order-types-limit-vs-stop)
6. [Price Data & Spot Subscriptions](#price-data--spot-subscriptions)
7. [Execution Events & Position Tracking](#execution-events--position-tracking)
8. [SL/TP Handling](#sltp-handling)
9. [Duplicate Prevention](#duplicate-prevention)
10. [Common Gotchas & Pitfalls](#common-gotchas--pitfalls)
11. [Configuration Options](#configuration-options)
12. [Database Schema Requirements](#database-schema-requirements)

---

## Architecture Overview

```
┌─────────────────────┐     ┌──────────────────────┐     ┌─────────────────┐
│  Signal Source      │────▶│  CTraderForex        │────▶│  CTraderPending │
│  (Telegram/DB)      │     │  ProcessorService    │     │  OrderService   │
└─────────────────────┘     └──────────────────────┘     └────────┬────────┘
                                                                  │
                                                                  ▼
┌─────────────────────┐     ┌──────────────────────┐     ┌─────────────────┐
│  TradeExecution     │◀────│  Execution Event     │◀────│  CTraderOrder   │
│  Queue (DB)         │     │  Handler             │     │  Manager        │
└─────────────────────┘     └──────────────────────┘     └────────┬────────┘
                                                                  │
                                                                  ▼
                                                         ┌─────────────────┐
                                                         │  cTrader Open   │
                                                         │  API (WebSocket)│
                                                         └─────────────────┘
```

---

## Key Components

### 1. CTraderClient (`ICTraderClient`)
- Manages WebSocket connection to cTrader Open API
- Handles authentication (application + account level)
- Provides message sending/receiving capabilities
- Fires `MessageReceived` events for all incoming messages

### 2. CTraderOrderManager (`ICTraderOrderManager`)
- Creates orders (Market, Limit, Stop)
- Modifies positions (SL/TP amendments)
- Cancels orders
- Closes positions
- Gets current bid/ask prices

### 3. CTraderPendingOrderService (`ICTraderPendingOrderService`)
- Orchestrates the complete order flow
- Handles signal processing
- Tracks pending orders awaiting fill
- Persists trades to database
- Sends Telegram notifications

### 4. CTraderSymbolService (`ICTraderSymbolService`)
- Caches symbol information (IDs, names, digits, volume constraints)
- Maps asset names to cTrader symbol IDs
- Provides volume normalization

### 5. CTraderForexProcessorService
- Background service polling for unprocessed signals
- Manages provider configurations (TakeOriginal/TakeOpposite)
- Coordinates original and opposite order creation

---

## Connection & Authentication

### Two-Stage Authentication

```csharp
// Stage 1: Application Authentication
var appAuthReq = new ProtoOAApplicationAuthReq
{
    ClientId = "your-client-id",
    ClientSecret = "your-client-secret"
};
await client.SendMessageAsync(appAuthReq, ProtoOAPayloadType.ProtoOaApplicationAuthReq);

// Stage 2: Account Authentication (after receiving ProtoOaApplicationAuthRes)
var accountAuthReq = new ProtoOAAccountAuthReq
{
    CtidTraderAccountId = accountId,
    AccessToken = "oauth-access-token"
};
await client.SendMessageAsync(accountAuthReq, ProtoOAPayloadType.ProtoOaAccountAuthReq);
```

### Important Notes:
- **Access Token**: Obtain via OAuth2 flow with cTrader
- **Account ID**: The `CtidTraderAccountId` (not the login number)
- **Demo vs Live**: Use different account IDs for each environment

---

## Order Flow

### Step 1: Signal Processing

```csharp
public async Task<CTraderOrderResult> ProcessSignalAsync(ParsedSignal signal, bool isOpposite = false)
{
    // 1. Determine effective direction (flip if isOpposite)
    var effectiveDirection = GetEffectiveDirection(signal.Direction, isOpposite);

    // 2. Check for duplicates (in-memory + DB)
    // 3. Infer order type (LIMIT vs STOP) based on current price
    // 4. Create order via OrderManager
    // 5. Track pending order or handle immediate fill
}
```

### Step 2: Order Type Inference

**CRITICAL**: The order type (LIMIT vs STOP) depends on the relationship between entry price and current market price.

| Direction | Entry vs Market | Order Type |
|-----------|-----------------|------------|
| BUY       | Entry < Market  | LIMIT      |
| BUY       | Entry > Market  | STOP       |
| SELL      | Entry > Market  | LIMIT      |
| SELL      | Entry < Market  | STOP       |

```csharp
// BUY: entry below/at market => LIMIT; above market => STOP
if (effectiveDirection == TradeDirection.Buy)
    return entry <= current ? CTraderOrderType.Limit : CTraderOrderType.Stop;

// SELL: entry above/at market => LIMIT; below market => STOP
if (effectiveDirection == TradeDirection.Sell)
    return entry >= current ? CTraderOrderType.Limit : CTraderOrderType.Stop;
```

### Step 3: Order Creation

```csharp
var orderReq = new ProtoOANewOrderReq
{
    CtidTraderAccountId = accountId,
    SymbolId = symbolId,
    OrderType = ProtoOAOrderType.Limit,  // or Stop
    TradeSide = ProtoOATradeSide.Buy,    // or Sell
    Volume = volume,                      // In centi-units (0.01 lot = 1000)
    LimitPrice = entryPrice,             // For LIMIT orders
    // OR
    StopPrice = entryPrice,              // For STOP orders
    StopLoss = stopLossPrice,            // Optional
    TakeProfit = takeProfitPrice         // Optional
};
```

### Step 4: Wait for Execution Event

```csharp
// Listen for ProtoOAExecutionEvent
if (execEvent.ExecutionType == ProtoOAExecutionType.OrderFilled ||
    execEvent.ExecutionType == ProtoOAExecutionType.OrderPartialFill)
{
    // Order filled - extract PositionId, ExecutionPrice
    var positionId = execEvent.Order.PositionId;
    var executedPrice = execEvent.Order.ExecutionPrice;
}
```

---

## Order Types: LIMIT vs STOP

### Why This Matters

**Placing the wrong order type causes immediate fills!**

- **LIMIT BUY at or above market price** → Fills immediately at market
- **LIMIT SELL at or below market price** → Fills immediately at market
- **STOP BUY at or below market price** → Fills immediately at market
- **STOP SELL at or above market price** → Fills immediately at market

### Marketability Check

Always validate before placing:

```csharp
bool isMarketable = false;
if (effectiveDirection == TradeDirection.Buy)
{
    if (orderType == CTraderOrderType.Limit && entryPrice >= marketPrice)
        isMarketable = true;
    if (orderType == CTraderOrderType.Stop && entryPrice <= marketPrice)
        isMarketable = true;
}
else if (effectiveDirection == TradeDirection.Sell)
{
    if (orderType == CTraderOrderType.Limit && entryPrice <= marketPrice)
        isMarketable = true;
    if (orderType == CTraderOrderType.Stop && entryPrice >= marketPrice)
        isMarketable = true;
}

if (isMarketable)
{
    // REJECT - would fill immediately
    return new CTraderOrderResult { Success = false, ErrorMessage = "Marketable order" };
}
```

---

## Price Data & Spot Subscriptions

### Subscribe to Spot Prices

Before fetching prices, you must subscribe:

```csharp
var req = new ProtoOASubscribeSpotsReq
{
    CtidTraderAccountId = accountId
};
req.SymbolId.Add(symbolId);
await client.SendMessageAsync(req, ProtoOAPayloadType.ProtoOaSubscribeSpotsReq);
```

### Receive Spot Events

```csharp
client.MessageReceived += (sender, msg) =>
{
    if (msg.PayloadType == ProtoOAPayloadType.ProtoOaSpotEvent)
    {
        var spot = ProtoOASpotEvent.Parser.ParseFrom(msg.Payload);
        var bid = spot.Bid;  // May be scaled integer
        var ask = spot.Ask;  // May be scaled integer
    }
};
```

### Price Normalization

**CRITICAL**: cTrader returns prices as scaled integers!

```csharp
private double NormalizePrice(long symbolId, object? raw)
{
    var asLong = Convert.ToInt64(raw);

    // Get symbol's decimal digits (e.g., 5 for EURUSD)
    if (_symbolService.TryGetSymbolDigits(symbolId, out var digits))
    {
        return asLong / Math.Pow(10, digits);
    }

    // Fallback: assume 5 decimal places
    return asLong / 100000d;
}
```

---

## Execution Events & Position Tracking

### Event Types to Handle

| ExecutionType | Meaning |
|---------------|---------|
| `OrderAccepted` | Pending order placed successfully |
| `OrderFilled` | Order fully filled → Position opened |
| `OrderPartialFill` | Order partially filled |
| `OrderCancelled` | Order cancelled |
| `OrderExpired` | Order expired |
| `OrderModified` | SL/TP modified |
| `PositionClosed` | Position closed (by SL/TP or manually) |

### Tracking Pending Orders

```csharp
private readonly Dictionary<long, PendingExecutionWatch> _pendingExecutions = new();

// When order is accepted (not immediately filled):
lock (_pendingLock)
{
    _pendingExecutions[orderId] = new PendingExecutionWatch
    {
        OrderId = orderId,
        Signal = signal,
        EffectiveDirection = effectiveDirection,
        IsOpposite = isOpposite,
        CreatedAt = DateTime.UtcNow
    };
}

// When fill event received:
lock (_pendingLock)
{
    if (_pendingExecutions.TryGetValue(orderId, out var watch))
    {
        _pendingExecutions.Remove(orderId);
        // Handle fill...
    }
}
```

### Extracting PositionId

PositionId can be in different locations depending on protobuf version:

```csharp
private static long? TryExtractPositionId(object executionEvent)
{
    var candidates = new (string Parent, string Child)[]
    {
        ("", "PositionId"),
        ("Position", "PositionId"),
        ("Order", "PositionId")
    };

    foreach (var (parent, child) in candidates)
    {
        // Try to extract from each location...
    }
    return null;
}
```

---

## SL/TP Handling

### For LIMIT/STOP Orders

SL/TP can be set at order creation:

```csharp
var orderReq = new ProtoOANewOrderReq
{
    // ... other fields ...
    StopLoss = stopLossPrice,
    TakeProfit = takeProfitPrice
};
```

### For MARKET Orders

**CRITICAL**: cTrader rejects absolute SL/TP on MARKET orders!

You must apply SL/TP after the order fills:

```csharp
// After receiving OrderFilled event with PositionId:
var amendReq = new ProtoOAAmendPositionSLTPReq
{
    CtidTraderAccountId = accountId,
    PositionId = positionId,
    StopLoss = stopLossPrice,
    TakeProfit = takeProfitPrice
};
await client.SendMessageAsync(amendReq, ProtoOAPayloadType.ProtoOaAmendPositionSltpReq);
```

### Common Error: TRADING_BAD_STOPS

This occurs when SL/TP is invalid (e.g., already crossed by market):

```csharp
if (errorCode == "TRADING_BAD_STOPS")
{
    // Try SL-only, then TP-only
    await SendAmendPositionSltpAsync(positionId, stopLoss, null);
    await SendAmendPositionSltpAsync(positionId, null, takeProfit);
}
```

---

## Duplicate Prevention

### Multi-Layer Protection

1. **In-Memory Tracking** (per-process lifetime):
```csharp
private readonly Dictionary<(int SignalId, bool IsOpposite), long> _placedOrdersBySignalLeg = new();

lock (_placedOrdersLock)
{
    if (_placedOrdersBySignalLeg.ContainsKey((signal.SignalId, isOpposite)))
    {
        return new CTraderOrderResult { Success = false, ErrorMessage = "Duplicate" };
    }
}
```

2. **Database-Level Tracking** (across restarts):
```csharp
if (await _repository.IsSignalProcessedAsync(signal.SignalId))
{
    return new CTraderOrderResult { Success = false, ErrorMessage = "Already processed" };
}
```

3. **Mark as Processed After Success**:
```csharp
await _repository.MarkSignalAsProcessedAsync(signal.SignalId);
```

### Opposite Order Handling

**Important**: When using `TakeOpposite=true`, don't check DB-level processed status for the opposite leg:

```csharp
// DB-level check ONLY for the original order (not opposite)
if (!isOpposite && await _repository.IsSignalProcessedAsync(signal.SignalId))
{
    return failure;
}
```

This allows:
1. Original order fills immediately → marks signal as processed
2. Opposite order can still be created (checked only in-memory)

---

## Common Gotchas & Pitfalls

### 1. Wrong Order Type = Immediate Fill

**Problem**: Placing a LIMIT BUY above market price fills immediately.

**Solution**: Always infer order type from current price vs entry price.

### 2. Processed Flag Blocks Opposite Order

**Problem**: Original order fills, marks signal processed, opposite order blocked.

**Solution**: Check DB-level processed only for original, use in-memory tracking per-leg.

### 3. Price Scaling

**Problem**: Prices from cTrader are scaled integers (e.g., 134750000 for 1.34750).

**Solution**: Normalize using symbol's digits or divide by 100000.

### 4. Volume Units

**Problem**: Volume is in centi-units, not lots.

**Solution**:
- Forex: `lots * 100000` (0.01 lot = 1000)
- Synthetics: Check `MinVolume`, `MaxVolume`, `StepVolume` from symbol info

```csharp
var volume = (long)Math.Round(lotSize * 100_000d);

// Apply constraints
if (volume < minVolume) volume = minVolume;
if (volume > maxVolume) volume = maxVolume;
volume = (volume / stepVolume) * stepVolume;
```

### 5. No Explicit Response for Some Operations

**Problem**: Some operations (like AmendPositionSLTP) may not send an explicit response.

**Solution**: Treat "no error within timeout" as success:

```csharp
var completed = await Task.WhenAny(errorTask, Task.Delay(timeout));
if (completed != errorTask)
{
    // No error received → treat as success
    return (true, null, null);
}
```

### 6. Symbol Mismatch in Execution Events

**Problem**: Execution event may be for a different symbol.

**Solution**: Always verify `SymbolId` matches before processing:

```csharp
if (TryExtractSymbolId(response, out var executedSymbolId))
{
    if (executedSymbolId != expectedSymbolId)
    {
        return new CTraderOrderResult { Success = false, ErrorMessage = "Symbol mismatch" };
    }
}
```

### 7. Spot Subscription Timing

**Problem**: Price unavailable immediately after subscription.

**Solution**: Wait or retry:

```csharp
(bid, ask) = await _orderManager.GetCurrentBidAskAsync(symbol);
if (!bid.HasValue && !ask.HasValue)
{
    await Task.Delay(500);  // Wait for ticks to arrive
    (bid, ask) = await _orderManager.GetCurrentBidAskAsync(symbol);
}
```

### 8. Opposite Order SL/TP Calculation

**Problem**: Opposite direction needs mirrored SL/TP.

**Solution**: Mirror distances from entry:

```csharp
// Original: BUY @ 1.3000, SL @ 1.2950, TP @ 1.3100
// Opposite: SELL @ 1.3000, SL @ 1.3050, TP @ 1.2900

decimal AdjustStopLoss(decimal? stopLoss)
{
    var dist = Math.Abs(entry - stopLoss.Value);
    return oppositeDirection == TradeDirection.Buy
        ? entry - dist   // SL below for BUY
        : entry + dist;  // SL above for SELL
}

decimal AdjustTakeProfit(decimal? takeProfit)
{
    var dist = Math.Abs(takeProfit.Value - entry);
    return oppositeDirection == TradeDirection.Buy
        ? entry + dist   // TP above for BUY
        : entry - dist;  // TP below for SELL
}
```

### 9. Synthetic Indices vs Forex

**Problem**: Synthetics (Volatility indices) behave differently.

**Solution**: Use different price sources:
- Forex: cTrader spot prices
- Synthetics: Deriv tick stream (more reliable)

```csharp
if (IsSyntheticAsset(signal.Asset))
{
    var price = await derivPriceProbe.ProbeAsync();
    // Use Deriv price for order type inference
}
else
{
    var (bid, ask) = await _orderManager.GetCurrentBidAskAsync(signal.Asset);
    // Use cTrader price
}
```

---

## Configuration Options

```json
{
  "CTrader": {
    "Environment": "Demo",  // or "Live"
    "DemoAccountId": "12345678",
    "LiveAccountId": "87654321",
    "ClientId": "your-client-id",
    "ClientSecret": "your-client-secret",
    "AccessToken": "your-oauth-token",
    "DefaultLotSize": "0.01",
    "SignalPollIntervalSeconds": 5,
    "MarkSignalsAsProcessedOnOriginalSuccess": true,
    "MarkSignalsAsProcessedOnFailure": false,
    "RequireSltpAmendSuccessToMarkProcessed": false
  }
}
```

---

## Database Schema Requirements

### ParsedSignalsQueue

```sql
CREATE TABLE ParsedSignalsQueue (
    SignalId INT PRIMARY KEY IDENTITY,
    Asset NVARCHAR(50),
    Direction NVARCHAR(10),  -- Buy, Sell
    EntryPrice DECIMAL(18, 8),
    StopLoss DECIMAL(18, 8),
    TakeProfit DECIMAL(18, 8),
    TakeProfit2 DECIMAL(18, 8),
    TakeProfit3 DECIMAL(18, 8),
    TakeProfit4 DECIMAL(18, 8),
    ProviderChannelId BIGINT,
    ProviderName NVARCHAR(100),
    SignalType NVARCHAR(20),
    RawMessage NVARCHAR(MAX),
    Processed BIT DEFAULT 0,
    ProcessedAt DATETIME2,
    ReceivedAt DATETIME2 DEFAULT GETUTCDATE()
);
```

### ForexTrades

```sql
CREATE TABLE ForexTrades (
    TradeId INT PRIMARY KEY IDENTITY,
    PositionId BIGINT,  -- cTrader PositionId
    Symbol NVARCHAR(50),
    Direction NVARCHAR(10),
    EntryPrice DECIMAL(18, 8),
    ExitPrice DECIMAL(18, 8),
    SL DECIMAL(18, 8),
    TP DECIMAL(18, 8),
    EntryTime DATETIME2,
    ExitTime DATETIME2,
    PnL DECIMAL(18, 2),
    Status NVARCHAR(20),  -- OPEN, CLOSED
    Outcome NVARCHAR(20), -- Profit, Loss, Breakeven
    RR NVARCHAR(20),      -- Risk:Reward ratio
    Strategy NVARCHAR(100),
    Notes NVARCHAR(MAX),
    TelegramMessageId INT,
    CreatedAt DATETIME2 DEFAULT GETUTCDATE()
);
```

### ProviderChannelConfig

```sql
CREATE TABLE ProviderChannelConfig (
    ProviderChannelId BIGINT PRIMARY KEY,
    ProviderName NVARCHAR(100),
    TakeOriginal BIT DEFAULT 1,
    TakeOpposite BIT DEFAULT 0,
    IsActive BIT DEFAULT 1
);
```

### TradeExecutionQueue

```sql
CREATE TABLE TradeExecutionQueue (
    QueueId INT PRIMARY KEY IDENTITY,
    CTraderOrderId NVARCHAR(50),  -- Actually stores PositionId
    Asset NVARCHAR(50),
    Direction NVARCHAR(10),
    StrategyName NVARCHAR(100),
    ProviderChannelId BIGINT,
    IsOpposite BIT DEFAULT 0,
    Processed BIT DEFAULT 0,
    ProcessedAt DATETIME2,
    CreatedAt DATETIME2 DEFAULT GETUTCDATE()
);
```

---

## Summary Checklist

When implementing cTrader integration:

- [ ] Implement two-stage authentication (App + Account)
- [ ] Subscribe to spot prices before fetching
- [ ] Normalize prices (scaled integers → decimals)
- [ ] Infer order type (LIMIT/STOP) from current price
- [ ] Check for marketable orders before placing
- [ ] Handle SL/TP differently for MARKET vs LIMIT/STOP orders
- [ ] Track pending orders in-memory
- [ ] Listen for execution events (OrderFilled, PositionClosed, etc.)
- [ ] Extract PositionId from various event locations
- [ ] Implement duplicate prevention (in-memory + DB)
- [ ] Handle opposite orders without double-flip
- [ ] Mirror SL/TP for opposite direction
- [ ] Use correct volume units (centi-lots)
- [ ] Apply volume constraints (min/max/step)
- [ ] Handle TRADING_BAD_STOPS errors gracefully
- [ ] Verify symbol ID matches in execution events

---

## Useful Protobuf Types

```csharp
// Request types
ProtoOANewOrderReq          // Create order
ProtoOAAmendPositionSLTPReq // Modify SL/TP
ProtoOACancelOrderReq       // Cancel pending order
ProtoOAClosePositionReq     // Close position
ProtoOASubscribeSpotsReq    // Subscribe to prices

// Response/Event types
ProtoOAExecutionEvent       // Order/position updates
ProtoOASpotEvent            // Price ticks
ProtoOAErrorRes             // Errors

// Enums
ProtoOAOrderType            // Market, Limit, Stop
ProtoOATradeSide            // Buy, Sell
ProtoOAExecutionType        // OrderFilled, OrderCancelled, etc.
```

---

*Document generated from DerivCTraderAutomation codebase*

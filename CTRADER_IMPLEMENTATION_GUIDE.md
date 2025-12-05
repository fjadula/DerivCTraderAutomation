# cTrader Integration Implementation Guide

## Current Status ✅ vs Missing ❌

### ✅ Already Implemented
- `CTraderClient` - WebSocket connection, receive loop, heartbeat
- `CTraderAuthenticator` - App auth + Account auth flows
- `CTraderOrderManager` - Order creation skeleton with BuyLimit/SellLimit logic
- `CTraderPositionMonitor` - Message processing, execution event handling
- Proto message types:
  - `ProtoOAApplicationAuthReq/Res`
  - `ProtoOAAccountAuthReq/Res`
  - `ProtoOANewOrderReq`
  - `ProtoOAExecutionEvent`
  - `ProtoOAPayloadType` enum
- `OrderExecutedEventArgs` - Event data for order executions

### ❌ Critical Missing Pieces

## 1. Symbol ID Mapping (Required for Order Placement)

**Problem**: cTrader needs symbolId (e.g., 1 for EURUSD), not symbol names
**Location**: `src/DerivCTrader.Infrastructure/CTrader/`
**Impact**: ❌ Orders will fail without this

**Solution: Create `SymbolMapper.cs`**
```csharp
public static class SymbolMapper
{
    private static readonly Dictionary<string, long> SymbolIds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Major Pairs
        ["EURUSD"] = 1,
        ["GBPUSD"] = 3,
        ["USDJPY"] = 4,
        ["AUDUSD"] = 8,
        ["USDCAD"] = 2,
        ["USDCHF"] = 6,
        ["NZDUSD"] = 9,
        
        // Crosses
        ["EURJPY"] = 13,
        ["GBPJPY"] = 17,
        ["GBPCAD"] = 50,
        // ... add more as needed
    };

    public static long GetSymbolId(string symbol) => 
        SymbolIds.TryGetValue(symbol, out var id) 
            ? id 
            : throw new ArgumentException($"Unknown symbol: {symbol}");
}
```

---

## 2. Tick Stream Subscription (Required for Price Monitoring)

**Problem**: cTrader won't send price ticks unless we subscribe
**Location**: `src/DerivCTrader.Infrastructure/CTrader/Models/`
**Impact**: ❌ Can't detect price crosses without ticks

**Solution: Create `ProtoOASpotSubscriptionEvent.cs`**
```csharp
namespace DerivCTrader.Infrastructure.CTrader.Models;

public class ProtoOASpotSubscriptionEvent : IMessage<ProtoOASpotSubscriptionEvent>
{
    public long CtidTraderAccountId { get; set; }
    public long SymbolId { get; set; }
    public decimal Bid { get; set; }
    public decimal Ask { get; set; }
    public long TradeTime { get; set; }  // Unix timestamp in milliseconds
    
    public static MessageParser<ProtoOASpotSubscriptionEvent> Parser { get; } = 
        new MessageParser<ProtoOASpotSubscriptionEvent>(() => new ProtoOASpotSubscriptionEvent());

    // ... IMessage implementation
}
```

**Then in `ProtoOAPayloadType.cs`:**
```csharp
ProtoOaSpotSubscriptionRes = 2151  // Add this
```

---

## 3. Price Cross Detection Logic

**Problem**: Currently we handle execution events but don't monitor pending orders for price crosses
**Location**: `src/DerivCTrader.Infrastructure/CTrader/CTraderOrderManager.cs`
**Impact**: ❌ Pending orders never execute

**Solution: Add to `CTraderOrderManager`**
```csharp
private Dictionary<string, PendingOrderMonitor> _pendingOrders = new();

public class PendingOrderMonitor
{
    public string OrderId { get; set; }
    public long SymbolId { get; set; }
    public decimal EntryPrice { get; set; }
    public string Direction { get; set; }  // "BUY" or "SELL"
    public decimal LastBid { get; set; }
    public decimal LastAsk { get; set; }
}

public bool CheckPriceCross(PendingOrderMonitor order, decimal newBid, decimal newAsk)
{
    order.LastBid = newBid;
    order.LastAsk = newAsk;

    // BUY: price falls to Entry and is rising
    if (order.Direction == "BUY")
    {
        return newAsk <= order.EntryPrice && newAsk > (order.EntryPrice - 0.0001m);
    }

    // SELL: price rises to Entry and is falling
    if (order.Direction == "SELL")
    {
        return newBid >= order.EntryPrice && newBid < (order.EntryPrice + 0.0001m);
    }

    return false;
}
```

---

## 4. Pending Order Wait-For-Execution Pattern

**Problem**: Currently we place pending orders but don't wait for them to execute
**Location**: `src/DerivCTrader.Infrastructure/CTrader/CTraderOrderManager.cs`
**Impact**: ❌ TradeExecutor gets called before orders actually execute

**Solution: Modify `CreateOrderAsync` flow**
```csharp
public async Task<CTraderOrderResult> CreateOrderAsync(
    ParsedSignal signal, 
    CTraderOrderType orderType, 
    bool isOpposite = false,
    CancellationToken cancellationToken = default)
{
    // 1. Place pending order
    var createResult = await _client.SendAsync(newOrderReq, cancellationToken);
    var orderId = ... // Extract from response
    
    // 2. Wait for execution (with timeout)
    var tcs = new TaskCompletionSource<CTraderOrderResult>();
    var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));  // 5 min timeout
    
    _client.ExecutionReceived += (sender, evt) =>
    {
        if (evt.OrderId == orderId && evt.ExecutionType == "ORDER_FILLED")
        {
            tcs.SetResult(new CTraderOrderResult
            {
                Success = true,
                OrderId = orderId,
                ExecutionPrice = evt.ExecutionPrice,
                ExecutedAt = DateTime.UtcNow
            });
        }
    };
    
    // 3. Wait or timeout
    var result = await Task.WhenAny(tcs.Task, Task.Delay(300000, timeoutCts.Token));
    if (!tcs.Task.IsCompleted) throw new TimeoutException("Order execution timeout");
    
    return await tcs.Task;
}
```

---

## 5. Handle Execution Confirmation (ProtoOANewOrderRes)

**Problem**: We don't confirm order was created server-side
**Location**: `src/DerivCTrader.Infrastructure/CTrader/Models/`
**Impact**: ⚠️ Medium - orders might fail silently

**Solution: Create `ProtoOANewOrderRes.cs`**
```csharp
public class ProtoOANewOrderRes : IMessage<ProtoOANewOrderRes>
{
    public long CtidTraderAccountId { get; set; }
    public string OrderId { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorDescription { get; set; } = string.Empty;
    public bool IsSuccessful { get; set; }
    
    // ... IMessage implementation
}
```

---

## 6. TradeExecutionQueue Integration

**Problem**: Currently nothing writes to TradeExecutionQueue after cTrader execution
**Location**: `src/DerivCTrader.Infrastructure/CTrader/CTraderService.cs`
**Impact**: ❌ Deriv never gets the binary order signal

**Solution: Modify execution event handler**
```csharp
private readonly ITradeRepository _repository;

public CTraderService(
    ILogger<CTraderService> logger,
    ICTraderClient client,
    ICTraderOrderManager orderManager,
    ITradeRepository repository)
{
    _logger = logger;
    _client = client;
    _orderManager = orderManager;
    _repository = repository;
    
    // Hook execution events
    _orderManager.OrderExecuted += async (sender, args) =>
    {
        await OnOrderExecutedAsync(args);
    };
}

private async Task OnOrderExecutedAsync(OrderExecutedEventArgs args)
{
    try
    {
        // 1. Log execution
        _logger.LogInformation(
            "✅ Order executed: {OrderId} {Asset} {Direction} @ {Price}",
            args.OrderId, args.Symbol, args.Direction, args.ExecutionPrice);

        // 2. Write to queue
        var queueItem = new TradeExecutionQueue
        {
            CTraderOrderId = args.OrderId,
            Asset = args.Symbol,
            Direction = args.Direction,
            CreatedAt = DateTime.UtcNow,
            StrategyName = args.StrategyName ?? "Unknown"
        };

        await _repository.EnqueueTradeAsync(queueItem);

        // 3. Signal TradeExecutor to process it
        OnTradeExecuted?.Invoke(this, queueItem);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to process executed order: {OrderId}", args.OrderId);
    }
}

public event EventHandler<TradeExecutionQueue>? OnTradeExecuted;
```

---

## 7. OrderExecutedEventArgs Enhancement

**Problem**: Current `OrderExecutedEventArgs` missing strategy context
**Location**: `src/DerivCTrader.Infrastructure/CTrader/Models/`

**Solution: Enhance the class**
```csharp
public class OrderExecutedEventArgs : EventArgs
{
    public string OrderId { get; set; }
    public string Symbol { get; set; }
    public string Direction { get; set; }
    public decimal ExecutionPrice { get; set; }
    public DateTime ExecutionTime { get; set; }
    public string? StrategyName { get; set; }
    public bool IsOpposite { get; set; }
}
```

---

## Implementation Priority

### 🔴 Critical (Do First - Blocks Everything)
1. Symbol ID Mapper
2. ProtoOASpotSubscriptionEvent  
3. Price cross detection logic

### 🟠 High (Needed for Pipeline)
4. Pending order wait-for-execution
5. ProtoOANewOrderRes handling
6. TradeExecutionQueue integration

### 🟡 Medium (Nice to Have)
7. OrderExecutedEventArgs enhancement
8. Better error handling & recovery

---

## Testing Strategy

### Phase 1: Unit Test Symbol Mapper
```csharp
[Fact]
public void SymbolMapper_EURUSD_Returns1()
{
    Assert.Equal(1, SymbolMapper.GetSymbolId("EURUSD"));
}
```

### Phase 2: Integration Test Price Cross
```csharp
[Fact]
public void PriceCross_BuyOrder_Detected()
{
    var order = new PendingOrderMonitor { ... };
    var crossed = orderManager.CheckPriceCross(order, 1.04999m, 1.05001m);
    Assert.True(crossed);
}
```

### Phase 3: End-to-End Test
1. Send test signal (EURUSD BUY at 1.05000)
2. Verify pending order created on cTrader
3. Simulate price cross (1.04999 → 1.05001)
4. Verify execution event received
5. Verify TradeExecutionQueue written
6. Verify Deriv binary executed

---

## Configuration Required

Add to `appsettings.json` (or already there):
```json
{
  "CTrader": {
    "ClientId": "YOUR_CLIENT_ID",
    "ClientSecret": "YOUR_CLIENT_SECRET",
    "AccessToken": "YOUR_ACCESS_TOKEN",
    "Environment": "Demo",
    "DemoAccountId": "2295141",
    "DefaultLotSize": "0.2",
    "HeartbeatIntervalSeconds": "25"
  }
}
```

---

## References

- cTrader Open API: https://spotware.github.io/Open-API/
- Proto Messages: https://spotware.github.io/open-api/messages/
- Symbol IDs: Check your cTrader account settings or get via `ProtoOAGetSymbolsReq`


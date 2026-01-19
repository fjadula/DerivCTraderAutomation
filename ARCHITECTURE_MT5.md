# MT5 System Architecture Documentation

## ?? High-Level Architecture (MT5 Version)

```
???????????????????????????????????????????????????????????????
?                   Telegram/Discord Providers                ?
?  ChartSense ? VIPFX ? PERFECTFX ? VIP_CHANNEL ? Others     ?
???????????????????????????????????????????????????????????????
                         ?
                         ?
         ?????????????????????????????????
         ?   WTelegramClient (Account1)  ?
         ?   WTelegramClient (Account2)  ?
         ?     Telegram.Bot (@AllDerivBot)?
         ?????????????????????????????????
                         ?
                         ?
         ?????????????????????????????????
         ?    SignalScraper Service      ?
         ?  • Listen for messages        ?
         ?  • Extract text/images        ?
         ?  • Route to parsers           ?
         ?????????????????????????????????
                         ?
                         ?
         ?????????????????????????????????
         ?    Signal Parser Engine       ?
         ?  • Text parsers (regex)       ?
         ?  • Image parsers (OCR)        ?
         ?  • Pure binary detector       ?
         ?????????????????????????????????
                         ?
                         ?
         ?????????????????????????????????
         ?    Provider Config Loader     ?
         ?  • TakeOriginal?              ?
         ?  • TakeOpposite?              ?
         ?  • Stake/Lot size             ?
         ?????????????????????????????????
                         ?
          ???????????????????????????????
          ?                             ?
          ?                             ?
???????????????????          ???????????????????
?  Pure Binary?   ?          ?  Forex Signal?  ?
?  (VIP CHANNEL)  ?          ?  (Others)       ?
???????????????????          ???????????????????
         ?                            ?
         ?                            ?
         ?              ???????????????????????????
         ?              ?  Create Pending Orders  ?
         ?              ?  • Original direction   ?
         ?              ?  • Opposite direction   ?
         ?              ?    (if configured)      ?
         ?              ???????????????????????????
         ?                         ?
         ?                         ?
         ?              ???????????????????????????
         ?              ?   MT5 Price Monitor     ?
         ?              ?  • Tick stream via REST ?
         ?              ?  • Price cross detection?
         ?              ?  • Direction validation ?
         ?              ???????????????????????????
         ?                         ?
         ?                         ?
         ?              ???????????????????????????
         ?              ?   MT5 Order Executed    ?
         ?              ?  • Order executed       ?
         ?              ?  • Deal ticket obtained ?
         ?              ?  • Write to Queue table ?
         ?              ???????????????????????????
         ?                         ?
         ???????????????????????????
                       ?
                       ?
         ???????????????????????????????
         ?   Deriv Binary Executor     ?
         ?  • Calculate expiry         ?
         ?  • Execute binary trade     ?
         ?  • Get contract ID          ?
         ???????????????????????????????
                        ?
                        ?
         ???????????????????????????????
         ?   KhulaFxTradeMonitor       ?
         ?  (Your existing app)        ?
         ?  • Detects binary execution ?
         ?  • Reads contract details   ?
         ???????????????????????????????
                        ?
                        ?
         ???????????????????????????????
         ?    Queue Matching Engine    ?
         ?  • FIFO matching            ?
         ?  • Asset + Direction        ?
         ?  • Fill StrategyName        ?
         ?  • Delete matched row       ?
         ???????????????????????????????
                        ?
                        ?
         ???????????????????????????????
         ?     SQL Server Database     ?
         ?  • BinaryOptionTrades       ?
         ?  • ForexTrades              ?
         ?  • TradeIndicators          ?
         ?  • TradeExecutionQueue      ?
         ?  • ProviderChannelConfig    ?
         ???????????????????????????????
```

---

## ?? Execution Flow Sequences (MT5)

### Sequence 1: Forex Signal ? MT5 ? Deriv Binary

```
User posts signal in Telegram
    ?
WTelegramClient receives Update
    ?
Extract channelId + message text/image
    ?
Load ProviderChannelConfig
    ?
Route to appropriate ISignalParser
    ?
ParsedSignal created (Asset, Direction, Entry, SL, TP)
    ?
Check: TakeOriginal? TakeOpposite?
    ?
Create pending order(s) on MT5 (0.2 lots)
    ?
MT5 REST API: POST /OrderSend
  {
    "action": TRADE_ACTION_PENDING,
    "symbol": "GBPUSD",
    "volume": 0.2,
    "type": ORDER_TYPE_BUY_LIMIT | ORDER_TYPE_SELL_LIMIT,
    "price": entry_price,
    "sl": stop_loss,
    "tp": take_profit
  }
    ?
Poll MT5 REST API for order status
  GET /Orders?ticket={order_ticket}
    ?
Price touches entry level in CORRECT direction
    ?
Pending order ? Market execution (automatic by MT5)
    ?
Poll MT5 for execution:
  GET /Deals?ticket={order_ticket}
    ?
Deal detected (position opened)
    ?
Write to TradeExecutionQueue:
  - MT5TicketNumber (Deal ticket)
  - MT5PositionId (Position ID)
  - Asset
  - Direction
  - StrategyName (from provider)
  - ProviderChannelId
  - IsOpposite flag
    ?
Calculate expiry (15min or 30min)
    ?
Call Deriv WebSocket API:
  - buy contract
  - stake $20
  - expiry calculated
    ?
Deriv returns contract_id
    ?
KhulaFxTradeMonitor detects binary execution
    ?
Match with QueueTable (FIFO by Asset+Direction)
    ?
Update BinaryOptionTrades.StrategyName
    ?
Delete matched row from Queue
    ?
Write TradeIndicators (optional)
    ?
Done ?
```

### Sequence 2: Pure Binary Signal (VIP CHANNEL)

```
User posts "OPEN GBP/CAD PUT 15 MIN"
    ?
VipChannelParser detects SignalType.PureBinary
    ?
SKIP MT5 entirely
    ?
Immediately call Deriv API
    ?
Execute binary with 21min expiry
    ?
KhulaFxTradeMonitor logs to DB
    ?
Done ?
```

---

## ??? Project Structure (MT5 Version)

```
DerivMT5Automation/
??? src/
?   ??? DerivMT5.Domain/
?   ?   ??? Entities/
?   ?   ?   ??? ParsedSignal.cs
?   ?   ?   ??? ForexTrade.cs (Add: MT5TicketNumber, MT5PositionId)
?   ?   ?   ??? BinaryOptionTrade.cs
?   ?   ?   ??? TradeIndicator.cs
?   ?   ?   ??? TradeExecutionQueue.cs (Add: MT5TicketNumber, MT5PositionId)
?   ?   ?   ??? ProviderChannelConfig.cs
?   ?   ??? Enums/
?   ?       ??? TradeDirection.cs
?   ?       ??? TradeStatus.cs
?   ?       ??? SignalType.cs
?   ?
?   ??? DerivMT5.Application/
?   ?   ??? Interfaces/
?   ?   ?   ??? ISignalParser.cs
?   ?   ?   ??? ITradeRepository.cs
?   ?   ?   ??? IMT5Client.cs (NEW - replaces ICTraderClient)
?   ?   ?   ??? IDerivClient.cs
?   ?   ??? Parsers/
?   ?       ??? VipFxParser.cs
?   ?       ??? PerfectFxParser.cs
?   ?       ??? VipChannelParser.cs
?   ?       ??? ChartSenseParser.cs (TODO)
?   ?
?   ??? DerivMT5.Infrastructure/
?   ?   ??? Persistence/
?   ?   ?   ??? SqlServerTradeRepository.cs
?   ?   ??? Trading/
?   ?   ?   ??? DerivWebSocketClient.cs
?   ?   ?   ??? MT5RestClient.cs (NEW - replaces CTraderWebSocketClient)
?   ?   ??? ExpiryCalculation/
?   ?       ??? BinaryExpiryCalculator.cs
?   ?
?   ??? DerivMT5.SignalScraper/
?   ?   ??? Services/
?   ?   ?   ??? TelegramSignalScraperService.cs
?   ?   ??? Program.cs
?   ?   ??? appsettings.json
?   ?
?   ??? DerivMT5.TradeExecutor/
?       ??? Services/
?       ?   ??? MT5MonitorService.cs (NEW - replaces CTraderMonitorService)
?       ?   ??? MT5PendingOrderService.cs (NEW)
?       ?   ??? BinaryExecutionService.cs
?       ?   ??? QueueMatchingService.cs
?       ??? Program.cs
?       ??? appsettings.json
?
??? README.md
??? ARCHITECTURE_MT5.md
??? azure-pipelines.yml
??? .gitignore
```

---

## ?? External Integrations (MT5)

### 1. Telegram (WTelegramClient)
**Purpose**: Scrape signals from private channels

**Accounts**:
- Account 1: `+2781xxxxxxxxx` (API ID: 3xxxxxx)
- Account 2: `+37xxxx` (API ID: 1xxxx)

**Channels Monitored**:
- ChartSense (-1001200022443)
- VIPFX (-1001138473049)
- PERFECTFX (-1001446944855)
- TradingHubVIP (-1476865523)
- DeriveVIKnights (-1001304028537)
- SyntheticIndicesTrader (-1003204276456)
- VIP_CHANNEL (-1392143914)

### 2. MT5 REST API (NEW)
**Purpose**: Execute trades and monitor positions

**Integration Options**:

#### Option A: MetaApi.cloud (Recommended)
**Endpoint**: `https://mt-client-api-v1.agiliumtrade.agiliumtrade.ai`

**Key Features**:
- REST API + WebSocket for real-time updates
- Multi-broker support
- Cloud-based (no local MT5 installation required)
- 14-day free trial, then paid plans

**Key Methods**:
```http
POST /users/current/accounts/{accountId}/trade
GET /users/current/accounts/{accountId}/orders
GET /users/current/accounts/{accountId}/positions
GET /users/current/accounts/{accountId}/deals
```

**Request Example**:
```json
POST /trade
{
  "actionType": "ORDER_TYPE_BUY_LIMIT",
  "symbol": "GBPUSD",
  "volume": 0.2,
  "openPrice": 1.27500,
  "stopLoss": 1.28000,
  "takeProfit": 1.26500
}
```

**Response Example**:
```json
{
  "numericCode": 10009,
  "stringCode": "TRADE_RETCODE_DONE",
  "orderId": "46870472",
  "positionId": "46870472"
}
```

#### Option B: MQL5 REST Bridge (Custom)
**Setup**: Deploy Expert Advisor (EA) on MT5 that:
- Listens on local HTTP endpoint (e.g., `http://localhost:8080`)
- Receives JSON commands from C# application
- Executes MT5 API calls via MQL5
- Returns results as JSON

**Pros**:
- Free (no subscription)
- Full control

**Cons**:
- Requires MT5 running 24/7
- Custom EA development
- No cloud redundancy

#### Option C: ZeroMQ Bridge (Advanced)
**Setup**: Use ZeroMQ library to establish socket connection between C# and MT5 EA

**Pros**:
- Very fast (low latency)
- No HTTP overhead

**Cons**:
- More complex setup
- Requires MT5 running 24/7

**Recommendation**: Use **MetaApi.cloud** for production (robust, scalable, cloud-based)

### 3. Deriv API (WebSocket)
**Purpose**: Execute binary options trades

**Endpoint**: `wss://ws.binaryws.com/websockets/v3?app_id=109082`

**Key Methods**:
- `authorize`: Authenticate with API token
- `buy`: Purchase binary contract
- `proposal`: Get contract proposal (optional)

**Asset Mapping**:
- Forex: `frxEURUSD`, `frxGBPJPY`
- Volatility: `1HZ10V`, `1HZ25V`, `1HZ50V`
- Commodities: `frxXAUUSD`

### 4. SQL Server (Dapper)
**Purpose**: Persist all trade data

**Connection**: `Server=YOUR_SERVER;Database=khulafx;...`

**Tables Used**:
- `ForexTrades`: MT5 executions (add `MT5TicketNumber`, `MT5PositionId` columns)
- `BinaryOptionTrades`: Deriv binaries
- `TradeIndicators`: Strategy metadata
- `TradeExecutionQueue`: Matching queue (add `MT5TicketNumber`, `MT5PositionId` columns)
- `ProviderChannelConfig`: Provider settings

---

## ??? Configuration Management (MT5)

### appsettings.json Structure
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=YOUR_SERVER;Database=khulafx;..."
  },
  "Deriv": {
    "Token": "your_deriv_token",
    "AppId": "109082",
    "WebSocketUrl": "wss://ws.binaryws.com/websockets/v3"
  },
  "MT5": {
    "Provider": "MetaApi",  // "MetaApi" | "RestBridge" | "ZeroMQ"
    "MetaApi": {
      "Token": "your_metaapi_token",
      "AccountId": "your_mt5_account_id",
      "BaseUrl": "https://mt-client-api-v1.agiliumtrade.agiliumtrade.ai"
    },
    "RestBridge": {
      "Url": "http://localhost:8080",
      "Timeout": 5000
    },
    "TradingSettings": {
      "DefaultLotSize": 0.2,
      "MaxSlippagePoints": 10,
      "OrderExpirationMinutes": 60
    }
  },
  "Telegram": {
    "WTelegram": {
      "Account1": {
        "ApiId": "YOUR_API_ID",
        "ApiHash": "YOUR_API_HASH",
        "PhoneNumber": "+2781xxxxxxxx",
        "SessionPath": "WTelegramSession1.dat"
      },
      "Account2": { ... }
    }
  },
  "BinaryOptions": {
    "DefaultStake": 20.0,
    "PureBinaryExpiry": 21
  }
}
```

---

## ?? Key Differences: cTrader vs MT5

| Feature | cTrader (Current) | MT5 (New) |
|---------|-------------------|-----------|
| **Protocol** | WebSocket (Open API) | REST API (MetaApi/Custom) |
| **Connection** | Persistent WebSocket | HTTP polling or WebSocket (MetaApi) |
| **Order Execution** | Real-time event stream | Polling for order status |
| **Price Monitoring** | Tick stream via WebSocket | REST API polling or WebSocket (MetaApi) |
| **Authentication** | OAuth2 + Application/Account auth | API Token (MetaApi) or local socket |
| **Position Tracking** | `PositionId` (long) | `PositionId` (string) + `Ticket` (string) |
| **Price Format** | Integer (divide by 100000) | Decimal (native) |
| **SL/TP Modification** | Real-time event notifications | Poll for order/position changes |
| **Broker Support** | cTrader brokers only | Any MT5 broker (via MetaApi) |
| **Cloud Hosting** | N/A (requires local cTrader) | MetaApi provides cloud access |

---

## ?? Database Schema Changes (MT5)

### ForexTrades Table Updates

```sql
-- Add MT5-specific columns
ALTER TABLE ForexTrades
ADD MT5TicketNumber NVARCHAR(50) NULL,
    MT5PositionId NVARCHAR(50) NULL;

-- Create index for faster lookups
CREATE INDEX IX_ForexTrades_MT5PositionId 
ON ForexTrades(MT5PositionId);
```

### TradeExecutionQueue Table Updates

```sql
-- Add MT5-specific columns
ALTER TABLE TradeExecutionQueue
ADD MT5TicketNumber NVARCHAR(50) NULL,
    MT5PositionId NVARCHAR(50) NULL;

-- Update matching logic to use MT5PositionId
CREATE INDEX IX_TradeExecutionQueue_MT5PositionId 
ON TradeExecutionQueue(MT5PositionId);
```

---

## ?? MT5 Implementation Components

### 1. MT5RestClient.cs (NEW)

```csharp
public interface IMT5Client
{
    Task<MT5OrderResult> PlacePendingOrderAsync(
        string symbol, 
        TradeDirection direction, 
        decimal volume, 
        decimal price, 
        decimal? stopLoss, 
        decimal? takeProfit);
    
    Task<List<MT5Order>> GetPendingOrdersAsync();
    Task<List<MT5Position>> GetOpenPositionsAsync();
    Task<List<MT5Deal>> GetRecentDealsAsync(DateTime since);
    Task<bool> ModifyOrderAsync(string ticket, decimal? sl, decimal? tp);
    Task<bool> ClosePositionAsync(string positionId);
}

public class MT5RestClient : IMT5Client
{
    private readonly HttpClient _httpClient;
    private readonly string _accountId;
    private readonly string _apiToken;
    
    // Implementation using MetaApi.cloud REST endpoints
    // POST /trade, GET /orders, GET /positions, GET /deals
}
```

### 2. MT5PendingOrderService.cs (NEW)

```csharp
public class MT5PendingOrderService : BackgroundService
{
    private readonly IMT5Client _mt5Client;
    private readonly ITradeRepository _repository;
    private readonly ITelegramNotifier _telegram;
    
    // Replaces CTraderPendingOrderService
    // Polls MT5 for:
    // 1. Order executions (pending ? filled)
    // 2. Position closes (TP/SL/manual)
    // 3. SL/TP modifications
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckPendingOrderExecutionsAsync();
            await CheckPositionClosesAsync();
            await CheckSlTpModificationsAsync();
            await Task.Delay(1000, stoppingToken); // Poll every 1 second
        }
    }
}
```

### 3. MT5MonitorService.cs (NEW)

```csharp
public class MT5MonitorService : BackgroundService
{
    private readonly IMT5Client _mt5Client;
    
    // Monitors MT5 account for:
    // - New deals
    // - Balance changes
    // - Connection status
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime lastCheck = DateTime.UtcNow;
        
        while (!stoppingToken.IsCancellationRequested)
        {
            var deals = await _mt5Client.GetRecentDealsAsync(lastCheck);
            foreach (var deal in deals)
            {
                await ProcessDealAsync(deal);
            }
            lastCheck = DateTime.UtcNow;
            await Task.Delay(2000, stoppingToken);
        }
    }
}
```

---

## ?? Migration Path: cTrader ? MT5

### Phase 1: Setup MT5 Integration (Week 1-2)
1. ? Sign up for MetaApi.cloud account
2. ? Connect MT5 broker account to MetaApi
3. ? Test REST API connectivity
4. ? Implement `MT5RestClient.cs`
5. ? Test order placement/cancellation

### Phase 2: Core Services (Week 3-4)
1. ? Implement `MT5PendingOrderService.cs`
2. ? Implement `MT5MonitorService.cs`
3. ? Update `SqlServerTradeRepository.cs` (add MT5 columns)
4. ? Test order execution flow
5. ? Test Telegram threading with MT5 positions

### Phase 3: Testing & Validation (Week 5)
1. ? Run on MT5 demo account
2. ? Verify all signals execute correctly
3. ? Verify queue matching works with MT5 tickets
4. ? Test SL/TP modifications
5. ? Test position close detection

### Phase 4: Production Deployment (Week 6)
1. ? Switch to MT5 live account
2. ? Deploy to VPS
3. ? Monitor for 1 week with small lot sizes
4. ? Scale up to full lot sizes

---

## ? Performance Considerations (MT5)

### Polling vs WebSocket

**REST API Polling**:
- ? Simple to implement
- ? Works with any broker
- ? Higher latency (1-2 second polling interval)
- ? More API calls (rate limits)

**MetaApi WebSocket** (Recommended):
- ? Real-time updates
- ? Lower latency
- ? Fewer API calls
- ? Slightly more complex setup

**Recommendation**: Use MetaApi WebSocket for real-time updates, fallback to REST polling

### Rate Limiting

MetaApi rate limits:
- **Free tier**: 50 requests/minute
- **Paid tier**: 500 requests/minute

**Optimization**:
- Batch requests where possible
- Cache order/position data
- Use WebSocket for real-time updates (doesn't count towards rate limit)

---

## ??? Error Handling Strategy (MT5)

### MT5-Specific Errors

**Order Execution Errors**:
- `TRADE_RETCODE_REQUOTE`: Price changed, retry with new price
- `TRADE_RETCODE_INVALID_STOPS`: SL/TP too close to entry, adjust
- `TRADE_RETCODE_NO_MONEY`: Insufficient margin, skip trade
- `TRADE_RETCODE_MARKET_CLOSED`: Market closed, queue for next open

**Connection Errors**:
- MetaApi authentication failed ? Retry with exponential backoff
- HTTP timeout ? Retry request
- Account disconnected ? Reconnect account

### Retry Strategy

```csharp
// Retry with exponential backoff
var retryPolicy = Policy
    .Handle<HttpRequestException>()
    .Or<TimeoutException>()
    .WaitAndRetryAsync(
        retryCount: 3,
        sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
        onRetry: (exception, timeSpan, retryCount, context) =>
        {
            _logger.LogWarning("MT5 API call failed. Retry {RetryCount} after {Delay}s", 
                retryCount, timeSpan.TotalSeconds);
        });
```

---

## ?? Monitoring & Observability (MT5)

### Key Metrics to Track

**MT5-Specific Metrics**:
- MT5 API call latency
- MT5 API error rate
- Order execution time (signal ? MT5 execution)
- Pending order fill rate
- MT5 account balance
- MT5 connection uptime

**Dashboard Widgets** (Grafana):
- MT5 API health (green/red)
- Recent orders (last 10)
- Open positions count
- Daily P&L chart
- Signal ? Execution delay (seconds)

---

## ?? Security Considerations (MT5)

### API Token Security

**MetaApi Token**:
- Store in Azure Key Vault or environment variable
- Never commit to Git
- Rotate every 90 days

**MT5 Account Credentials**:
- Use read-only investor password for monitoring
- Use trading password only for execution service
- Enable 2FA on broker account

---

## ?? Troubleshooting Guide (MT5)

### Common Issues

**Issue**: Orders not executing
- Check MT5 account connection status via MetaApi dashboard
- Verify symbol is available on broker
- Check margin requirements
- Review MT5 trade logs

**Issue**: Position not found after execution
- Wait 2-3 seconds for position to appear
- Check if order was rejected (insufficient margin)
- Verify `MT5PositionId` is correctly stored

**Issue**: SL/TP modification not working
- MT5 may reject if new SL/TP too close to current price
- Check broker's minimum stop level
- Verify position still open

---

## ?? Summary: MT5 vs cTrader

### Why MT5?

**Pros**:
- ? More brokers support MT5 (wider broker choice)
- ? Cloud access via MetaApi (no local installation)
- ? Lower spreads (MT5 brokers often more competitive)
- ? Better liquidity (more popular platform)

**Cons**:
- ? No native WebSocket API (requires MetaApi or custom bridge)
- ? Polling-based (slightly higher latency than cTrader WebSocket)
- ? MetaApi subscription cost (~$50-100/month)

### When to Choose MT5

Choose MT5 if:
- Your broker doesn't support cTrader
- You want cloud-based access (MetaApi)
- You need multi-broker support
- You prioritize lower spreads

Choose cTrader if:
- Your broker supports cTrader
- You want native WebSocket API (lower latency)
- You prefer no third-party dependencies (MetaApi)
- You're already invested in cTrader ecosystem

---

**Last Updated**: December 16, 2025  
**Version**: 1.0 (MT5)  
**Author**: Trading Automation Team

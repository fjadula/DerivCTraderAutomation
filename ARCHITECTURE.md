# System Architecture Documentation

## 📐 High-Level Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                   Telegram/Discord Providers                │
│  ChartSense │ VIPFX │ PERFECTFX │ VIP_CHANNEL │ Others     │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
         ┌───────────────────────────────┐
         │   WTelegramClient (Account1)  │
         │   WTelegramClient (Account2)  │
         │     Telegram.Bot (@AllDerivBot)│
         └───────────────┬───────────────┘
                         │
                         ▼
         ┌───────────────────────────────┐
         │    SignalScraper Service      │
         │  • Listen for messages        │
         │  • Extract text/images        │
         │  • Route to parsers           │
         └───────────────┬───────────────┘
                         │
                         ▼
         ┌───────────────────────────────┐
         │    Signal Parser Engine       │
         │  • Text parsers (regex)       │
         │  • Image parsers (OCR)        │
         │  • Pure binary detector       │
         └───────────────┬───────────────┘
                         │
                         ▼
         ┌───────────────────────────────┐
         │    Provider Config Loader     │
         │  • TakeOriginal?              │
         │  • TakeOpposite?              │
         │  • Stake/Lot size             │
         └───────────────┬───────────────┘
                         │
          ┌──────────────┴──────────────┐
          │                             │
          ▼                             ▼
┌─────────────────┐          ┌─────────────────┐
│  Pure Binary?   │          │  Forex Signal?  │
│  (VIP CHANNEL)  │          │  (Others)       │
└────────┬────────┘          └────────┬────────┘
         │                            │
         │                            ▼
         │              ┌─────────────────────────┐
         │              │  Create Pending Orders  │
         │              │  • Original direction   │
         │              │  • Opposite direction   │
         │              │    (if configured)      │
         │              └──────────┬──────────────┘
         │                         │
         │                         ▼
         │              ┌─────────────────────────┐
         │              │  cTrader Price Monitor  │
         │              │  • WebSocket tick stream│
         │              │  • Price cross detection│
         │              │  • Correct direction    │
         │              └──────────┬──────────────┘
         │                         │
         │                         ▼
         │              ┌─────────────────────────┐
         │              │  cTrader Order Executed │
         │              │  • Event fired          │
         │              │  • Write to Queue table │
         │              └──────────┬──────────────┘
         │                         │
         └─────────────┬───────────┘
                       │
                       ▼
         ┌─────────────────────────────┐
         │   Deriv Binary Executor     │
         │  • Calculate expiry         │
         │  • Execute binary trade     │
         │  • Get contract ID          │
         └──────────────┬──────────────┘
                        │
                        ▼
         ┌─────────────────────────────┐
         │   KhulaFxTradeMonitor       │
         │  (Your existing app)        │
         │  • Detects binary execution │
         │  • Reads contract details   │
         └──────────────┬──────────────┘
                        │
                        ▼
         ┌─────────────────────────────┐
         │    Queue Matching Engine    │
         │  • FIFO matching            │
         │  • Asset + Direction        │
         │  • Fill StrategyName        │
         │  • Delete matched row       │
         └──────────────┬──────────────┘
                        │
                        ▼
         ┌─────────────────────────────┐
         │     SQL Server Database     │
         │  • BinaryOptionTrades       │
         │  • ForexTrades              │
         │  • TradeIndicators          │
         │  • TradeExecutionQueue      │
         │  • ProviderChannelConfig    │
         └─────────────────────────────┘
```

---

## 🔄 Execution Flow Sequences

### Sequence 1: Forex Signal → cTrader → Deriv Binary

```
User posts signal in Telegram
    ↓
WTelegramClient receives Update
    ↓
Extract channelId + message text/image
    ↓
Load ProviderChannelConfig
    ↓
Route to appropriate ISignalParser
    ↓
ParsedSignal created (Asset, Direction, Entry, SL, TP)
    ↓
Check: TakeOriginal? TakeOpposite?
    ↓
Create pending order(s) on cTrader (0.2 lots)
    ↓
cTrader price monitor watches for cross
    ↓
Price touches entry level in CORRECT direction
    ↓
Pending order → Market execution
    ↓
OrderExecuted event fired
    ↓
Write to TradeExecutionQueue:
  - CTraderOrderId
  - Asset
  - Direction
  - StrategyName (from provider)
  - IsOpposite flag
    ↓
Calculate expiry (15min or 30min)
    ↓
Call Deriv WebSocket API:
  - buy contract
  - stake $20
  - expiry calculated
    ↓
Deriv returns contract_id
    ↓
KhulaFxTradeMonitor detects binary execution
    ↓
Match with QueueTable (FIFO by Asset+Direction)
    ↓
Update BinaryOptionTrades.StrategyName
    ↓
Delete matched row from Queue
    ↓
Write TradeIndicators (optional)
    ↓
Done ✓
```

### Sequence 2: Pure Binary Signal (VIP CHANNEL)

```
User posts "OPEN GBP/CAD PUT 15 MIN"
    ↓
VipChannelParser detects SignalType.PureBinary
    ↓
SKIP cTrader entirely
    ↓
Immediately call Deriv API
    ↓
Execute binary with 21min expiry
    ↓
KhulaFxTradeMonitor logs to DB
    ↓
Done ✓
```

---

## 🗂️ Project Structure

```
DerivCTraderAutomation/
├── src/
│   ├── DerivCTrader.Domain/
│   │   ├── Entities/
│   │   │   ├── ParsedSignal.cs
│   │   │   ├── ForexTrade.cs
│   │   │   ├── BinaryOptionTrade.cs
│   │   │   ├── TradeIndicator.cs
│   │   │   ├── TradeExecutionQueue.cs
│   │   │   └── ProviderChannelConfig.cs
│   │   └── Enums/
│   │       ├── TradeDirection.cs
│   │       ├── TradeStatus.cs
│   │       └── SignalType.cs
│   │
│   ├── DerivCTrader.Application/
│   │   ├── Interfaces/
│   │   │   ├── ISignalParser.cs
│   │   │   ├── ITradeRepository.cs
│   │   │   ├── ICTraderClient.cs
│   │   │   └── IDerivClient.cs
│   │   └── Parsers/
│   │       ├── VipFxParser.cs
│   │       ├── PerfectFxParser.cs
│   │       ├── VipChannelParser.cs
│   │       └── ChartSenseParser.cs (TODO)
│   │
│   ├── DerivCTrader.Infrastructure/
│   │   ├── Persistence/
│   │   │   └── SqlServerTradeRepository.cs
│   │   ├── Trading/
│   │   │   ├── DerivWebSocketClient.cs
│   │   │   └── CTraderWebSocketClient.cs (TODO)
│   │   └── ExpiryCalculation/
│   │       └── BinaryExpiryCalculator.cs
│   │
│   ├── DerivCTrader.SignalScraper/
│   │   ├── Services/
│   │   │   └── TelegramSignalScraperService.cs
│   │   ├── Program.cs
│   │   └── appsettings.json
│   │
│   └── DerivCTrader.TradeExecutor/
│       ├── Services/
│       │   ├── CTraderMonitorService.cs (TODO)
│       │   ├── BinaryExecutionService.cs (TODO)
│       │   └── QueueMatchingService.cs (TODO)
│       ├── Program.cs
│       └── appsettings.json
│
├── README.md
├── ARCHITECTURE.md
├── azure-pipelines.yml
└── .gitignore
```

---

## 🔌 External Integrations

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
- DeriveVIKnights (@DeriveVIKnightsPtY)
- SyntheticIndicesTrader (-1679549617)
- VIP_CHANNEL (-1392143914)

### 2. Deriv API (WebSocket)
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

### 3. cTrader API (WebSocket / Open API)
**Purpose**: Monitor prices and execute pending orders

**Environment**: Demo (`2295141`) → Live (configurable)

**Key Operations**:
- Place pending order (BuyLimit/SellLimit)
- Monitor tick stream
- Detect price cross
- Execute market order
- Cancel expired orders

### 4. SQL Server (Dapper)
**Purpose**: Persist all trade data

**Connection**: `Server=YOUR_SERVER;Database=khulafx;...`

**Tables Used**:
- `ForexTrades`: cTrader executions
- `BinaryOptionTrades`: Deriv binaries
- `TradeIndicators`: Strategy metadata
- `TradeExecutionQueue`: Matching queue
- `ProviderChannelConfig`: Provider settings

---

## 🎛️ Configuration Management

### Environment Variables
- `ASPNETCORE_ENVIRONMENT`: `Development` | `Production`
- `ConnectionStrings__DefaultConnection`: SQL connection string

### appsettings.json Structure
```json
{
  "ConnectionStrings": { ... },
  "Deriv": { ... },
  "CTrader": {
    "Environment": "Demo",  // ← Switch to "Live"
    "DemoAccountId": "2295141",
    "LiveAccountId": ""
  },
  "Telegram": { ... },
  "BinaryOptions": {
    "DefaultStake": 20.0,  // ← Easily configurable
    "PureBinaryExpiry": 21
  }
}
```

---

## ⚡ Performance Considerations

### Concurrency
- **TPL Dataflow** for signal processing pipeline
- **Dedicated HostedServices** for price watchers
- **Async/await** throughout for non-blocking I/O

### Database Optimization
- **Dapper** for raw SQL performance
- **AsNoTracking** (no EF Core overhead)
- **Connection pooling** enabled
- **Batch writes** where possible

### WebSocket Efficiency
- **Single persistent connection** per service
- **Auto-reconnect** with exponential backoff
- **Message queuing** for reliability

---

## 🛡️ Error Handling Strategy

### Levels of Resilience

**1. Transient Errors** (Retry)
- Network timeouts
- WebSocket disconnections
- SQL deadlocks

**2. Validation Errors** (Skip)
- Unparseable signals
- Missing provider config
- Invalid symbols

**3. Critical Errors** (Alert + Fail)
- Database connection lost
- Deriv API authentication failed
- cTrader API unreachable

### Logging Strategy

```
Information → Normal flow (parsed signals, executed trades)
Warning     → Recoverable issues (missing config, retry attempts)
Error       → Failed operations (API errors, DB errors)
Fatal       → System-wide failures (startup failures)
```

---

## 🔄 Deployment Pipeline

### CI/CD Flow

```
Developer commits → GitHub
    ↓
Azure Pipelines trigger
    ↓
Restore NuGet packages
    ↓
Build solution (Release mode)
    ↓
Run unit tests (if any)
    ↓
Publish SignalScraper (win-x64 self-contained)
    ↓
Publish TradeExecutor (win-x64 self-contained)
    ↓
Upload artifacts
    ↓
Deploy to VPS (if main branch)
    ↓
Stop Windows Services
    ↓
Copy new binaries
    ↓
Start Windows Services
    ↓
Health check
```

---

## 🚀 Scalability Roadmap

### Phase 1: MVP (Current)
- 2 console applications
- File-based logging
- Manual configuration

### Phase 2: Enhanced (Next 3 months)
- Web Admin Panel (ASP.NET Core)
- Database logging
- Prometheus metrics
- Grafana dashboards

### Phase 3: Distributed (Next 6 months)
- Message queue (RabbitMQ/Azure Service Bus)
- Microservices architecture
- Kubernetes deployment
- Horizontal scaling

---

## 📈 Monitoring & Observability

### Key Metrics to Track

**Business Metrics**:
- Signals received per hour
- Signals parsed successfully
- Trades executed (cTrader)
- Binaries executed (Deriv)
- Win rate by provider
- Average P&L per provider

**Technical Metrics**:
- WebSocket connection uptime
- Database query latency
- Queue depth
- Memory usage
- CPU usage

### Health Checks
- SQL Server connectivity
- Deriv API connectivity
- cTrader API connectivity
- WTelegram session validity

---

## 🔐 Security Considerations

### Secrets Management
- ❌ Never commit `appsettings.json` to Git
- ✅ Use Azure Key Vault (production)
- ✅ Use environment variables
- ✅ Encrypt sensitive DB columns

### API Security
- Use least-privilege API tokens
- Rotate tokens regularly
- Monitor for unusual activity
- Rate limiting on API calls

---

## 📞 Troubleshooting Guide

See README.md for common issues and solutions.

---

**Last Updated**: November 2024  
**Version**: 1.0  
**Author**: Trading Automation Team

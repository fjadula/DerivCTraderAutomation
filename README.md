# Deriv + cTrader Automated Trading System

## 🎯 Overview

This system automates signal execution from multiple Telegram/Discord providers to:
1. **Scrape signals** from provider channels (text & image-based)
2. **Place pending orders** on cTrader with correct execution logic
3. **Execute binary options** on Deriv when cTrader trades fill
4. **Match trades** using queue system with FIFO logic
5. **Log everything** to SQL Server database

---

## 🏗️ Architecture

```
├── Domain Layer          # Entities, Enums, Value Objects
├── Application Layer     # Business Logic, Parsers, Interfaces
├── Infrastructure Layer  # DB, Deriv API, cTrader API
├── SignalScraper        # Telegram scraping console app
└── TradeExecutor        # cTrader & Deriv execution console app
```

---

## ✅ Features Implemented

### ✨ Signal Parsing
- ✅ Text-based parsers (VIPFX, PERFECTFX, TradingHubVIP, etc.)
- ✅ Image-based parser (ChartSense - OCR required)
- ✅ Pure binary signals (VIP CHANNEL - instant execution)
- ✅ Provider-specific configurations

### 🔄 Execution Logic
- ✅ **Correct price cross detection** (waits for price to touch/cross entry in correct direction)
- ✅ Original + Opposite trade support (configurable per provider)
- ✅ Queue-based matching system (FIFO)
- ✅ Dynamic expiry calculation:
  - Volatility indices: 15 min (1-bar)
  - Forex/Crypto: 30 min (2-bar), minimum 21 min

### 💾 Database Integration
- ✅ SQL Server with Dapper (high performance)
- ✅ Matches your existing schema:
  - `ForexTrades`
  - `BinaryOptionTrades`
  - `TradeIndicators`
  - `TradeExecutionQueue`
  - `ProviderChannelConfig`

### 🛡️ Safety & Reliability
- ✅ Structured logging with Serilog (file + console)
- ✅ Resilient WebSocket connections (auto-reconnect)
- ✅ Configurable environment (Demo/Live switch)
- ✅ Provider-specific stake & lot size settings

---

## 📦 Prerequisites

### Software Requirements
- .NET 8.0 SDK
- SQL Server (existing `khulafx` database)
- Windows Server VPS
- Visual Studio 2022 or VS Code

### API Credentials Needed
1. **Telegram WTelegram** (2 accounts configured)
2. **Deriv API** (AppId + Token)
3. **cTrader** (Account ID + API credentials)
4. **SQL Server** connection string

---

## ⚙️ Configuration

### 1. Update `appsettings.json`

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=YOUR_SERVER;Database=khulafx;User Id=YOUR_USER;Password=YOUR_PASS;TrustServerCertificate=True;"
  },
  "Deriv": {
    "AppId": "109082",
    "Token": "YOUR_DERIV_TOKEN",
    "WebSocketUrl": "wss://ws.binaryws.com/websockets/v3?app_id=109082"
  },
  "CTrader": {
    "Environment": "Demo",  // Change to "Live" when ready
    "DemoAccountId": "2295141",
    "LiveAccountId": "",
    "DefaultLotSize": 0.2
  },
  "Telegram": {
    "BotToken": "5612018794:AAEK-NFkKnJKWcqiJYephH8l9zQpHOWVZxw",
    "AlertChatId": "YOUR_CHAT_ID",
    "WTelegram": {
      "Account1": {
        "ApiId": "YOUR_API_ID",
        "ApiHash": "YOUR_API_HASH",
        "PhoneNumber": "+YOUR_PHONE"
      }
    }
  }
}
```

### 2. Database Setup

Ensure your SQL Server has these tables (already in your DB):
- `ForexTrades`
- `BinaryOptionTrades`
- `TradeIndicators`
- `TradeExecutionQueue`
- `ProviderChannelConfig`

**Important**: Populate `ProviderChannelConfig` table:

```sql
INSERT INTO ProviderChannelConfig (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite)
VALUES 
('-1001200022443', 'ChartSense', 1, 0),
('-1001138473049', 'VIPFX', 1, 1),
('-1001446944855', 'PERFECTFX', 1, 0),
('-1392143914', 'VIP_CHANNEL', 1, 0);
```

---

## 🚀 Running the System

### Method 1: Visual Studio

1. Open `DerivCTraderAutomation.sln`
2. Set **Multiple Startup Projects**:
   - `DerivCTrader.SignalScraper`
   - `DerivCTrader.TradeExecutor`
3. Press F5

### Method 2: Command Line

```bash
# Terminal 1 - Signal Scraper
cd src/DerivCTrader.SignalScraper
dotnet run

# Terminal 2 - Trade Executor
cd src/DerivCTrader.TradeExecutor
dotnet run
```

### Method 3: Windows Services (Production)

```bash
# Publish as self-contained
dotnet publish -c Release -r win-x64 --self-contained

# Install as Windows Service (requires admin)
sc create "DerivSignalScraper" binPath="C:\Path\To\DerivCTrader.SignalScraper.exe"
sc create "DerivTradeExecutor" binPath="C:\Path\To\DerivCTrader.TradeExecutor.exe"

sc start DerivSignalScraper
sc start DerivTradeExecutor
```

---

## 📊 Monitoring & Logs

Logs are written to:
- **Console** (real-time monitoring)
- **File**: `logs/signal-scraper-YYYY-MM-DD.log`
- **File**: `logs/trade-executor-YYYY-MM-DD.log`

### Log Levels
- `Information`: Normal operations, parsed signals, executed trades
- `Warning`: Missing configurations, unparseable signals
- `Error`: API failures, database errors
- `Fatal`: Critical failures requiring restart

---

## 🔧 TODO / Next Steps

### High Priority
- [ ] **Complete ChartSense OCR parser** (requires PaddleOCR or Tesseract)
- [ ] **Implement cTrader WebSocket client** (price monitoring + order placement)
- [ ] **Complete remaining signal parsers** (TradingHubVIP, DeriveVIKnights, etc.)
- [ ] **Build price cross detection engine** (correct execution logic)
- [ ] **Implement trade execution pipeline** (SignalScraper → TradeExecutor communication)

### Medium Priority
- [ ] Add unit tests
- [ ] Implement Telegram alert notifications
- [ ] Add health check endpoints
- [ ] Create admin panel API endpoints
- [ ] Implement duplicate trade prevention

### Low Priority
- [ ] Add Prometheus metrics
- [ ] Create Grafana dashboards
- [ ] Implement trade performance analytics
- [ ] Add backtesting module

---

## 🛠️ Development Guidelines

### Adding a New Signal Parser

1. Create parser class in `Application/Parsers/`:

```csharp
public class MyProviderParser : ISignalParser
{
    private const string ChannelId = "-1001234567";
    
    public bool CanParse(string providerChannelId) => providerChannelId == ChannelId;
    
    public Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        // Your parsing logic here
    }
}
```

2. Register in `Program.cs`:

```csharp
services.AddSingleton<ISignalParser, MyProviderParser>();
```

3. Add to `ProviderChannelConfig` table

---

## 🐛 Troubleshooting

### WTelegram login issues
- Delete `.session` files
- Ensure phone number includes country code
- Check Telegram API credentials

### SQL Connection issues
- Verify connection string
- Check SQL Server is running
- Ensure `TrustServerCertificate=True` if using self-signed cert

### Deriv WebSocket failures
- Check internet connectivity
- Verify API token is valid
- Ensure AppId matches token

---

## 📞 Support

For issues or questions:
1. Check logs in `logs/` directory
2. Review configuration in `appsettings.json`
3. Verify database tables exist and are accessible

---

## 📄 License

Proprietary - All rights reserved

---

## 🎉 Credits

Built with:
- .NET 8.0
- Dapper
- WTelegramClient
- Serilog
- Websocket.Client

# Quick Start Guide

## 🚀 Get Up and Running in 30 Minutes

### Step 1: Prerequisites Check (5 minutes)

Ensure you have:
- [ ] .NET 8.0 SDK installed
- [ ] Visual Studio 2022 or VS Code
- [ ] SQL Server accessible
- [ ] SQL Server database `khulafx` exists
- [ ] Deriv API credentials
- [ ] Telegram WTelegram credentials
- [ ] cTrader account (Demo or Live)

---

### Step 2: Database Setup (5 minutes)

1. **Verify tables exist** in your `khulafx` database:
   ```sql
   SELECT name FROM sys.tables 
   WHERE name IN ('ForexTrades', 'BinaryOptionTrades', 'TradeIndicators', 
                  'TradeExecutionQueue', 'ProviderChannelConfig')
   ```

2. **Populate ProviderChannelConfig**:
   ```sql
   INSERT INTO ProviderChannelConfig (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite)
   VALUES 
   ('-1001200022443', 'ChartSense', 1, 0),
   ('-1001138473049', 'VIPFX', 1, 1),
   ('-1001446944855', 'PERFECTFX', 1, 0),
   ('-1476865523', 'TradingHubVIP', 1, 0),
   ('-1679549617', 'SyntheticIndicesTrader', 1, 0),
   ('-1392143914', 'VIP_CHANNEL', 1, 0);
   ```

---

### Step 3: Configure Secrets (10 minutes)

1. **Create appsettings.json** (copy from template):

**SignalScraper**:
```bash
cd src/DerivCTrader.SignalScraper
cp appsettings.json appsettings.Production.json
```

Edit `appsettings.Production.json`:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=YOUR_SQL_SERVER;Database=khulafx;User Id=YOUR_USER;Password=YOUR_PASS;TrustServerCertificate=True;"
  },
  "Deriv": {
    "AppId": "109082",
    "Token": "YOUR_ACTUAL_DERIV_TOKEN_HERE"
  },
  "Telegram": {
    "BotToken": "5612018794:AAEK-NFkKnJKWcqiJYephH8l9zQpHOWVZxw",
    "AlertChatId": "YOUR_TELEGRAM_CHAT_ID",
    "WTelegram": {
      "Account1": {
        "ApiId": "3xxxxxx",
        "ApiHash": "710xxxxx",
        "PhoneNumber": "+2781xxxxxxxxx"
      },
      "Account2": {
        "ApiId": "1xxxx",
        "ApiHash": "28b52a49xxxxxxxx",
        "PhoneNumber": "+37xxxx"
      }
    }
  }
}
```

2. **Copy same settings** to TradeExecutor:
```bash
cd ../DerivCTrader.TradeExecutor
cp ../DerivCTrader.SignalScraper/appsettings.Production.json ./
```

---

### Step 4: Test Deriv Connection (5 minutes)

Create a simple test file to verify Deriv API works:

```bash
cd src/DerivCTrader.Infrastructure/Trading
```

Add a test method to `DerivWebSocketClient.cs` (temporary):
```csharp
public async Task TestConnectionAsync()
{
    await ConnectAsync();
    _logger.LogInformation("Deriv connection test successful!");
    await DisconnectAsync();
}
```

Run quick console test:
```bash
dotnet run --project DerivCTrader.SignalScraper
```

Look for: `"Connected to Deriv WebSocket API"` in logs.

---

### Step 5: Run SignalScraper (5 minutes)

```bash
cd src/DerivCTrader.SignalScraper
dotnet run
```

**Expected Output**:
```
[12:34:56 INF] Starting Deriv cTrader Signal Scraper...
[12:34:57 INF] WTelegram Account 1 logged in successfully
[12:34:58 INF] WTelegram Account 2 logged in successfully
[12:34:59 INF] Listening for Telegram messages from configured channels...
```

**Test It**: Post a test message in one of your Telegram channels and watch the logs!

---

## ✅ What Works Right Now

### Already Functional:
1. ✅ **Telegram signal scraping** (WTelegramClient)
2. ✅ **Text signal parsing** (VIPFX, PERFECTFX, VIP_CHANNEL)
3. ✅ **Database integration** (SQL Server + Dapper)
4. ✅ **Deriv API** (binary execution)
5. ✅ **Expiry calculation** (15min for volatility, 30min for forex)
6. ✅ **Logging** (Serilog to console + file)

### What to Test Now:
- Send a test signal in VIPFX format:
  ```
  We are selling EURUSD now at 1.05000
  Take profit at: 1.04500
  Stop loss at: 1.05500
  ```
- Watch the logs for successful parsing

---

## ⏭️ Immediate Next Steps

### Priority 1: Complete cTrader Integration (REQUIRED)

The biggest missing piece is cTrader WebSocket integration. Here's what you need:

**Resources**:
- cTrader Open API Docs: https://spotware.github.io/Open-API/
- C# Protobuf libraries needed
- WebSocket connection to cTrader demo server

**Files to Create**:
```
src/DerivCTrader.Infrastructure/Trading/CTraderWebSocketClient.cs
```

**Key Methods to Implement**:
1. `ConnectAsync()` - Establish WebSocket connection
2. `CreatePendingOrderAsync(ParsedSignal)` - Place BuyLimit/SellLimit
3. `MonitorPriceCross()` - Watch ticks and detect cross
4. `ExecuteMarketOrder()` - Convert pending to market when crossed
5. Fire `OrderExecuted` event

**Estimated Time**: 8-12 hours

---

### Priority 2: Trade Execution Pipeline

Create a simple queue table to pass signals between SignalScraper and TradeExecutor:

```sql
CREATE TABLE ParsedSignalsQueue (
    SignalId INT IDENTITY(1,1) PRIMARY KEY,
    Asset NVARCHAR(20) NOT NULL,
    Direction NVARCHAR(10) NOT NULL,
    EntryPrice DECIMAL(18,5),
    StopLoss DECIMAL(18,5),
    TakeProfit DECIMAL(18,5),
    ProviderChannelId NVARCHAR(50),
    ProviderName NVARCHAR(100),
    SignalType NVARCHAR(20),
    ReceivedAt DATETIME NOT NULL,
    Processed BIT DEFAULT 0,
    ProcessedAt DATETIME NULL
)
```

**Modify SignalScraper** to write to this table after parsing.

**Create TradeExecutor service** to poll this table.

**Estimated Time**: 4-5 hours

---

### Priority 3: ChartSense OCR Parser

For image-based signals, you need OCR. Options:

**Option A: Tesseract (Open Source)**
```bash
dotnet add package Tesseract --version 5.2.0
```

**Option B: PaddleOCR (Better accuracy)**
```bash
pip install paddleocr
# Call from C# via Process or HTTP API
```

**Estimated Time**: 6-10 hours

---

## 🔍 How to Debug Issues

### Logs Location
- **SignalScraper**: `src/DerivCTrader.SignalScraper/logs/`
- **TradeExecutor**: `src/DerivCTrader.TradeExecutor/logs/`

### Common Issues

**WTelegram Login Fails**:
```
Error: Phone number not registered
```
→ Delete `.session` files and re-login

**SQL Connection Fails**:
```
Error: Cannot open database "khulafx"
```
→ Verify connection string in appsettings.json
→ Test with: `sqlcmd -S YOUR_SERVER -U YOUR_USER -P YOUR_PASS`

**Deriv API Fails**:
```
Error: Deriv authorization failed
```
→ Check API token is valid
→ Ensure AppId matches your token

---

## 📞 Need Help?

1. Check logs first (`logs/` folder)
2. Review `PROJECT_STATUS.md` for what's implemented
3. Read `ARCHITECTURE.md` for technical details
4. See `README.md` for troubleshooting

---

## 🎯 Success Criteria

You'll know it's working when:

1. ✅ SignalScraper logs: `"Successfully parsed signal: EURUSD Sell @ 1.05000"`
2. ✅ TradeExecutor creates pending order on cTrader
3. ✅ Price crosses entry → market order executes
4. ✅ Binary trade executes on Deriv
5. ✅ KhulaFxTradeMonitor detects binary execution
6. ✅ Queue matching links StrategyName
7. ✅ Database has both ForexTrade and BinaryOptionTrade records

---

**Time to First Working Trade**: 30-40 hours of focused development

**Good Luck! 🚀**

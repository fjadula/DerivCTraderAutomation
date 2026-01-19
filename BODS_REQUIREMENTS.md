# BODS - Binary Options Daily Signals

## Project Overview

BODS is a standalone trading automation system that:
1. Scrapes trading signals from multiple Telegram channels
2. Parses signals using provider-specific parsers
3. Executes **Rise/Fall (CALL/PUT) binary options** on Deriv
4. Tracks trade results and sends notifications

**Key Differentiator from DerivCTraderAutomation:**
- NO cTrader integration
- NO MT5 integration
- ONLY PureBinary execution on Deriv
- Separate database (`BODS`)
- Provider-specific expiry times (default: 40 minutes)

---

## Technology Stack

| Component | Technology |
|-----------|------------|
| Runtime | .NET 10.0 |
| Database | SQL Server (Database: `BODS`) |
| ORM | Dapper (lightweight, SQL-first) |
| Telegram Client | WTelegram (user account scraping) |
| Deriv API | WebSocket (`wss://ws.binaryws.com/websockets/v3`) |
| Logging | Serilog |
| DI Container | Microsoft.Extensions.DependencyInjection |

---

## Project Structure

```
BODST/
├── BODST.Domain/
│   ├── Entities/
│   │   ├── BinaryOptionTrade.cs
│   │   ├── ForexTrade.cs
│   │   ├── ProviderChannelConfig.cs
│   │   └── ParsedSignal.cs
│   └── Enums/
│       ├── TradeDirection.cs
│       └── TradeResult.cs
├── BODST.Application/
│   ├── Interfaces/
│   │   ├── ISignalParser.cs
│   │   ├── ITradeRepository.cs
│   │   ├── IDerivClient.cs
│   │   └── ITelegramNotifier.cs
│   └── Parsers/
│       ├── JamsonTraderParser.cs
│       ├── FxPipsPredatorParser.cs
│       ├── ForexSignalsIoParser.cs
│       ├── XauusdGoldSignalParser.cs
│       └── TradeMasterParser.cs
├── BODST.Infrastructure/
│   ├── Persistence/
│   │   └── SqlServerTradeRepository.cs
│   ├── Deriv/
│   │   ├── DerivClient.cs
│   │   └── DerivAssetMapper.cs
│   └── Notifications/
│       └── TelegramNotifier.cs
└── BODST.Worker/
    ├── Program.cs
    ├── Services/
    │   ├── TelegramSignalScraperService.cs
    │   └── BinaryExecutionService.cs
    └── appsettings.json
```

---

## Database Schema

### Database: `BODS`

### Table: `ProviderChannelConfig`

```sql
CREATE TABLE [dbo].[ProviderChannelConfig] (
    [ProviderChannelId] NVARCHAR(50) PRIMARY KEY,  -- e.g., '-1001641563130'
    [ProviderName] NVARCHAR(100) NOT NULL,         -- e.g., 'JAMSONTRADER'
    [TakeOriginal] BIT NOT NULL DEFAULT 1,         -- Execute signal as-is
    [TakeOpposite] BIT NOT NULL DEFAULT 0,         -- Execute opposite direction
    [ExpiryTime] INT NOT NULL DEFAULT 40,          -- Expiry in MINUTES (40 = default)
    [IsActive] BIT NOT NULL DEFAULT 1,
    [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
```

**ExpiryTime Examples:**
- 5 = 5 minutes
- 40 = 40 minutes (default)
- 120 = 2 hours
- 2880 = 2 days (48 hours)

### Table: `BinaryOptionTrades`

```sql
CREATE TABLE [dbo].[BinaryOptionTrades] (
    [TradeId] INT IDENTITY(1,1) PRIMARY KEY,
    [AssetName] NVARCHAR(50) NOT NULL,             -- Deriv symbol (R_50, frxEURUSD)
    [Direction] NVARCHAR(10) NOT NULL,             -- 'CALL' or 'PUT'
    [OpenTime] DATETIME2 NULL,
    [CloseTime] DATETIME2 NULL,
    [ExpiryLength] INT NOT NULL,                   -- In minutes
    [Result] NVARCHAR(20) NULL,                    -- 'WIN', 'LOSS', 'TIE', 'PENDING'
    [ClosedBeforeExpiry] BIT NOT NULL DEFAULT 0,
    [SentToTelegramPublic] BIT NOT NULL DEFAULT 0,
    [SentToTelegramPrivate] BIT NOT NULL DEFAULT 0,
    [SentToWhatsApp] BIT NOT NULL DEFAULT 0,
    [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [ExpiryDisplay] NVARCHAR(20) NULL,             -- '5M', '40M', '2H'
    [TradeStake] DECIMAL(18,2) NOT NULL DEFAULT 1.00,
    [ExpectedExpiryTimestamp] DATETIME2 NULL,
    [EntryPrice] DECIMAL(18,5) NULL,
    [ExitPrice] DECIMAL(18,5) NULL,
    [StrategyName] NVARCHAR(100) NULL,             -- Provider name
    [PredictedOutcome] NVARCHAR(20) NULL,
    [PredictionConfidence] DECIMAL(5,2) NULL,
    [SignalGeneratedAt] DATETIME2 NULL,
    [SignalValidUntil] DATETIME2 NULL,
    [SentOpenToTelegram] BIT NOT NULL DEFAULT 0,
    [SentCloseToTelegram] BIT NOT NULL DEFAULT 0,
    [TelegramMessageId] INT NULL,                  -- Original signal message ID
    [DerivContractId] NVARCHAR(50) NULL,           -- Contract ID from Deriv
    [ProviderChannelId] NVARCHAR(50) NULL,
    [RawSignalText] NVARCHAR(MAX) NULL
);

CREATE INDEX IX_BinaryOptionTrades_CreatedAt ON BinaryOptionTrades(CreatedAt DESC);
CREATE INDEX IX_BinaryOptionTrades_Result ON BinaryOptionTrades(Result);
CREATE INDEX IX_BinaryOptionTrades_ProviderChannelId ON BinaryOptionTrades(ProviderChannelId);
```

### Table: `ForexTrades` (Future Use)

```sql
CREATE TABLE [dbo].[ForexTrades] (
    [TradeId] INT IDENTITY(1,1) PRIMARY KEY,
    [Symbol] NVARCHAR(20) NOT NULL,
    [Direction] NVARCHAR(10) NOT NULL,             -- 'BUY' or 'SELL'
    [EntryPrice] DECIMAL(18,5) NULL,
    [ExitPrice] DECIMAL(18,5) NULL,
    [EntryTime] DATETIME2 NULL,
    [ExitTime] DATETIME2 NULL,
    [PnL] DECIMAL(18,2) NULL,
    [PnLPercent] DECIMAL(10,4) NULL,
    [Status] NVARCHAR(20) NOT NULL DEFAULT 'PENDING',
    [Notes] NVARCHAR(MAX) NULL,
    [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [IndicatorsLinked] NVARCHAR(MAX) NULL,
    [StrategyName] NVARCHAR(100) NULL,
    [OriginalDirection] NVARCHAR(10) NULL,
    [FinalDirection] NVARCHAR(10) NULL,
    [StopLoss] DECIMAL(18,5) NULL,
    [TakeProfit] DECIMAL(18,5) NULL,
    [RRAtClose] DECIMAL(10,4) NULL,
    [CloseReason] NVARCHAR(100) NULL,
    [SentOpenToTelegramPrivate] BIT NOT NULL DEFAULT 0,
    [SentCloseToTelegramPrivate] BIT NOT NULL DEFAULT 0,
    [SentToTelegramPublic] BIT NOT NULL DEFAULT 0,
    [SentToWhatsApp] BIT NOT NULL DEFAULT 0,
    [PositionId] NVARCHAR(50) NULL,
    [Strategy] NVARCHAR(100) NULL,
    [Outcome] NVARCHAR(20) NULL,
    [TP] DECIMAL(18,5) NULL,
    [SL] DECIMAL(18,5) NULL,
    [RR] DECIMAL(10,4) NULL,
    [TelegramMessageId] INT NULL
);
```

### Table: `ParsedSignalsQueue` (Signal Buffer)

```sql
CREATE TABLE [dbo].[ParsedSignalsQueue] (
    [SignalId] INT IDENTITY(1,1) PRIMARY KEY,
    [Asset] NVARCHAR(50) NOT NULL,
    [Direction] NVARCHAR(10) NOT NULL,
    [EntryPrice] DECIMAL(18,5) NULL,
    [StopLoss] DECIMAL(18,5) NULL,
    [TakeProfit] DECIMAL(18,5) NULL,
    [ProviderChannelId] NVARCHAR(50) NOT NULL,
    [ProviderName] NVARCHAR(100) NULL,
    [ReceivedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [Processed] BIT NOT NULL DEFAULT 0,
    [ProcessedAt] DATETIME2 NULL,
    [RawMessage] NVARCHAR(MAX) NULL,
    [TelegramMessageId] INT NULL,

    CONSTRAINT UQ_ParsedSignals UNIQUE (Asset, Direction, ProviderChannelId, EntryPrice)
);
```

---

## Provider Configurations

### Initial Data Insert

```sql
-- JAMSONTRADER
INSERT INTO ProviderChannelConfig (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite, ExpiryTime, IsActive)
VALUES ('-1001641563130', 'JAMSONTRADER', 1, 0, 40, 1);

-- FXPIPSPREDATOR
INSERT INTO ProviderChannelConfig (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite, ExpiryTime, IsActive)
VALUES ('-1001235677912', 'FXPIPSPREDATOR', 1, 0, 40, 1);

-- FOREXSIGNALSIO
INSERT INTO ProviderChannelConfig (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite, ExpiryTime, IsActive)
VALUES ('-1001768939027', 'FOREXSIGNALSIO', 1, 0, 40, 1);

-- XAUUSD GOLD SIGNAL
INSERT INTO ProviderChannelConfig (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite, ExpiryTime, IsActive)
VALUES ('-1001419177246', 'XAUUSD_GOLD_SIGNAL', 1, 0, 40, 1);

-- TRADEMASTER
INSERT INTO ProviderChannelConfig (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite, ExpiryTime, IsActive)
VALUES ('-1002626150817', 'TRADEMASTER', 1, 0, 40, 1);
```

---

## Provider Signal Formats & Parser Specifications

### 1. JAMSONTRADER (`-1001641563130`)

**Format:**
```
XAUUSD BUY 4352
XAUUSD SELL 4352
```

**Parser Logic:**
```csharp
// Pattern: {ASSET} {BUY|SELL} {PRICE}
var pattern = @"^(\w+)\s+(BUY|SELL)\s+(\d+\.?\d*)";
```

**Direction Mapping:**
- BUY → CALL
- SELL → PUT

---

### 2. FXPIPSPREDATOR (`-1001235677912`)

**Format:**
```
#GOLD

BUY___4308-4304
SL___4297
TP___4316
TP2___4348

#GOLD_TRIAL🔥

BUY___4388-4381
SL___4375
TP___4397
TP2___4440
```

**Parser Logic:**
```csharp
// Direction and zone
var directionPattern = @"^(BUY|SELL)___(\d+\.?\d*)-(\d+\.?\d*)";
// Use midpoint of zone as entry, or first value

// SL/TP extraction
var slPattern = @"SL___(\d+\.?\d*)";
var tpPattern = @"TP___(\d+\.?\d*)";
var tp2Pattern = @"TP2___(\d+\.?\d*)";
```

**GOTCHA:** Asset is NOT in the message! Must infer from channel context or default to XAUUSD.

---

### 3. FOREXSIGNALSIO (`-1001768939027`)

**Format (Synthetic Indices):**
```
VOLATILITY 50 1S INDEX

  SELL NOW

APPLY PROPER RISK MANAGEMENT
```

**Format (With TP/SL):**
```
STEP INDEX BUY NOW 782

TP 7806
TP 7810
TP 7815

SL 7792
APPLY PROPER RISK MANAGEMENT
```

**Parser Logic:**
```csharp
// Asset and Direction pattern
var assetDirPattern = @"(VOLATILITY\s+\d+\s*(?:1S)?\s*INDEX|STEP\s*INDEX|BOOM\s*\d+|CRASH\s*\d+)\s+(BUY|SELL)\s+NOW";

// Entry price (optional, after direction)
var entryPattern = @"(BUY|SELL)\s+NOW\s*(\d+\.?\d*)?";

// TP lines
var tpPattern = @"TP\s+(\d+\.?\d*)";

// SL
var slPattern = @"SL\s+(\d+\.?\d*)";
```

**GOTCHA:** The phrase "APPLY PROPER RISK MANAGEMENT" should be ignored.

---

### 4. XAUUSD GOLD SIGNAL (`-1001419177246`)

**Format:**
```
Xauusd Sell 4374

TP 4371
TP 4368
TP 4365
TP 4361


SL 4381
```

**Parser Logic:**
```csharp
// Main signal line
var mainPattern = @"(\w+)\s+(Buy|Sell)\s+(\d+\.?\d*)";

// Multiple TPs
var tpPattern = @"TP\s+(\d+\.?\d*)";  // Capture all matches

// SL
var slPattern = @"SL\s+(\d+\.?\d*)";
```

**Case:** Note the mixed case (Xauusd, Sell) - use case-insensitive matching.

---

### 5. TRADEMASTER (`-1002626150817`)

**Format:**
```
XAUUSD SELL 4352

😄TP 4348
😄TP 4344
😄TP 4340
😄TP 4336

😄SL 4366
```

**Parser Logic:**
```csharp
// Main signal line
var mainPattern = @"(\w+)\s+(BUY|SELL)\s+(\d+\.?\d*)";

// TPs with emoji prefix
var tpPattern = @"(?:😄|[\U0001F600-\U0001F64F])?TP\s+(\d+\.?\d*)";

// SL with emoji prefix
var slPattern = @"(?:😄|[\U0001F600-\U0001F64F])?SL\s+(\d+\.?\d*)";
```

**GOTCHA:** Emoji handling - strip or include in pattern. Use Unicode ranges for flexibility.

---

## Deriv Asset Mapping

### All Available Rise/Fall Assets on Deriv

```csharp
public static class DerivAssetMapper
{
    private static readonly Dictionary<string, string> AssetMap = new()
    {
        // Volatility Indices
        { "VOLATILITY 10 INDEX", "R_10" },
        { "VOLATILITY 25 INDEX", "R_25" },
        { "VOLATILITY 50 INDEX", "R_50" },
        { "VOLATILITY 75 INDEX", "R_75" },
        { "VOLATILITY 100 INDEX", "R_100" },

        // Volatility 1s Indices
        { "VOLATILITY 10 1S INDEX", "1HZ10V" },
        { "VOLATILITY 25 1S INDEX", "1HZ25V" },
        { "VOLATILITY 50 1S INDEX", "1HZ50V" },
        { "VOLATILITY 75 1S INDEX", "1HZ75V" },
        { "VOLATILITY 100 1S INDEX", "1HZ100V" },

        // Boom Indices
        { "BOOM 300 INDEX", "BOOM300N" },
        { "BOOM 500 INDEX", "BOOM500" },
        { "BOOM 1000 INDEX", "BOOM1000" },

        // Crash Indices
        { "CRASH 300 INDEX", "CRASH300N" },
        { "CRASH 500 INDEX", "CRASH500" },
        { "CRASH 1000 INDEX", "CRASH1000" },

        // Step Index
        { "STEP INDEX", "stpRNG" },

        // Range Break Indices
        { "RANGE BREAK 100 INDEX", "RDBEAR" },
        { "RANGE BREAK 200 INDEX", "RDBULL" },

        // Jump Indices
        { "JUMP 10 INDEX", "JD10" },
        { "JUMP 25 INDEX", "JD25" },
        { "JUMP 50 INDEX", "JD50" },
        { "JUMP 75 INDEX", "JD75" },
        { "JUMP 100 INDEX", "JD100" },

        // Forex Pairs
        { "XAUUSD", "frxXAUUSD" },
        { "EURUSD", "frxEURUSD" },
        { "GBPUSD", "frxGBPUSD" },
        { "USDJPY", "frxUSDJPY" },
        { "AUDUSD", "frxAUDUSD" },
        { "USDCAD", "frxUSDCAD" },
        { "USDCHF", "frxUSDCHF" },
        // Add more as needed...
    };

    public static string MapToDerivSymbol(string rawAsset)
    {
        var normalized = rawAsset.ToUpperInvariant().Trim();

        // Try exact match first
        if (AssetMap.TryGetValue(normalized, out var symbol))
            return symbol;

        // Try partial matching for variations
        foreach (var kvp in AssetMap)
        {
            if (normalized.Contains(kvp.Key) || kvp.Key.Contains(normalized))
                return kvp.Value;
        }

        // Fallback: assume it's already a Deriv symbol
        return normalized;
    }
}
```

---

## Core Flow Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                        TELEGRAM CHANNELS                             │
│  JAMSONTRADER | FXPIPSPREDATOR | FOREXSIGNALSIO | GOLD | TRADEMASTER│
└───────────────────────────────┬─────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────────┐
│               TelegramSignalScraperService                           │
│  - WTelegram client listens to channels                              │
│  - OnUpdate receives new messages                                    │
│  - Routes to appropriate parser based on ProviderChannelId          │
└───────────────────────────────┬─────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────────┐
│                         PARSERS                                      │
│  JamsonTraderParser | FxPipsPredatorParser | ForexSignalsIoParser   │
│  XauusdGoldSignalParser | TradeMasterParser                         │
│                                                                      │
│  Output: ParsedSignal { Asset, Direction, Entry, SL, TP }           │
└───────────────────────────────┬─────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    ParsedSignalsQueue (Database)                     │
│  Buffer table - signals wait here for processing                    │
└───────────────────────────────┬─────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    BinaryExecutionService                            │
│  - Polls queue every 5 seconds                                       │
│  - Loads ExpiryTime from ProviderChannelConfig                      │
│  - Maps direction: BUY→CALL, SELL→PUT                               │
│  - Maps asset to Deriv symbol                                        │
│  - Calls DerivClient.PlaceBinaryOptionAsync()                       │
│  - Records trade in BinaryOptionTrades                               │
│  - Marks signal as processed                                         │
└───────────────────────────────┬─────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────────┐
│                         DERIV API                                    │
│  WebSocket: wss://ws.binaryws.com/websockets/v3?app_id=XXX          │
│  1. Authorize with token                                             │
│  2. Request proposal (get price quote)                               │
│  3. Buy contract                                                     │
│  4. Monitor for result (optional)                                    │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Configuration File

### appsettings.json

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning"
    }
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning"
      }
    },
    "WriteTo": [
      { "Name": "Console" },
      {
        "Name": "File",
        "Args": {
          "path": "logs/bods-.log",
          "rollingInterval": "Day"
        }
      }
    ]
  },
  "ConnectionStrings": {
    "BODS": "Server=YOUR_SERVER;Database=BODS;Trusted_Connection=True;TrustServerCertificate=True;"
  },
  "Deriv": {
    "AppId": "YOUR_APP_ID",
    "Token": "YOUR_API_TOKEN",
    "WebSocketUrl": "wss://ws.binaryws.com/websockets/v3"
  },
  "Trading": {
    "DefaultStake": 1.00,
    "DefaultExpiryMinutes": 40,
    "PollingIntervalSeconds": 5
  },
  "Telegram": {
    "BotToken": "YOUR_BOT_TOKEN",
    "WTelegram": {
      "ApiId": "YOUR_API_ID",
      "ApiHash": "YOUR_API_HASH",
      "PhoneNumber": "+YOUR_PHONE",
      "Password": "YOUR_2FA_PASSWORD"
    }
  },
  "ProviderChannels": {
    "JAMSONTRADER": "-1001641563130",
    "FXPIPSPREDATOR": "-1001235677912",
    "FOREXSIGNALSIO": "-1001768939027",
    "XAUUSD_GOLD_SIGNAL": "-1001419177246",
    "TRADEMASTER": "-1002626150817"
  }
}
```

---

## ISignalParser Interface

```csharp
public interface ISignalParser
{
    /// <summary>
    /// Check if this parser can handle messages from the given provider channel
    /// </summary>
    bool CanParse(string providerChannelId);

    /// <summary>
    /// Parse the message and return a signal, or null if parsing fails
    /// </summary>
    Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null);
}
```

---

## Deriv WebSocket API Reference

### 1. Connect
```
wss://ws.binaryws.com/websockets/v3?app_id=YOUR_APP_ID
```

### 2. Authorize
```json
{ "authorize": "YOUR_TOKEN", "req_id": 1 }
```

### 3. Get Proposal (Price Quote)
```json
{
  "proposal": 1,
  "amount": 1,
  "basis": "stake",
  "contract_type": "CALL",  // or "PUT"
  "currency": "USD",
  "duration": 40,
  "duration_unit": "m",     // m=minutes, h=hours, d=days
  "symbol": "R_50",
  "req_id": 2
}
```

### 4. Buy Contract
```json
{
  "buy": "PROPOSAL_ID_FROM_STEP_3",
  "price": 1.00,
  "req_id": 3
}
```

### 5. Monitor Contract (Optional)
```json
{
  "proposal_open_contract": 1,
  "contract_id": "CONTRACT_ID_FROM_BUY",
  "subscribe": 1,
  "req_id": 4
}
```

---

## Key Entities

### ParsedSignal.cs

```csharp
public class ParsedSignal
{
    public int SignalId { get; set; }
    public string Asset { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;  // BUY or SELL
    public decimal? EntryPrice { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public decimal? TakeProfit2 { get; set; }
    public decimal? TakeProfit3 { get; set; }
    public decimal? TakeProfit4 { get; set; }
    public string ProviderChannelId { get; set; } = string.Empty;
    public string? ProviderName { get; set; }
    public DateTime ReceivedAt { get; set; }
    public bool Processed { get; set; }
    public int? TelegramMessageId { get; set; }
    public string? RawMessage { get; set; }
}
```

### BinaryOptionTrade.cs

```csharp
public class BinaryOptionTrade
{
    public int TradeId { get; set; }
    public string AssetName { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;  // CALL or PUT
    public DateTime? OpenTime { get; set; }
    public DateTime? CloseTime { get; set; }
    public int ExpiryLength { get; set; }  // In minutes
    public string? Result { get; set; }  // WIN, LOSS, TIE, PENDING
    public bool ClosedBeforeExpiry { get; set; }
    public decimal TradeStake { get; set; }
    public DateTime? ExpectedExpiryTimestamp { get; set; }
    public decimal? EntryPrice { get; set; }
    public decimal? ExitPrice { get; set; }
    public string? StrategyName { get; set; }  // Provider name
    public string? DerivContractId { get; set; }
    public string? ProviderChannelId { get; set; }

    // Notification flags
    public bool SentToTelegramPublic { get; set; }
    public bool SentToTelegramPrivate { get; set; }
    public bool SentOpenToTelegram { get; set; }
    public bool SentCloseToTelegram { get; set; }
    public int? TelegramMessageId { get; set; }
}
```

### ProviderChannelConfig.cs

```csharp
public class ProviderChannelConfig
{
    public string ProviderChannelId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public bool TakeOriginal { get; set; } = true;
    public bool TakeOpposite { get; set; } = false;
    public int ExpiryTime { get; set; } = 40;  // Minutes
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}
```

---

## NuGet Packages Required

```xml
<ItemGroup>
    <!-- Core -->
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="8.0.0" />

    <!-- Database -->
    <PackageReference Include="Dapper" Version="2.1.35" />
    <PackageReference Include="Microsoft.Data.SqlClient" Version="5.2.0" />

    <!-- Telegram -->
    <PackageReference Include="WTelegram" Version="4.0.0" />

    <!-- Logging -->
    <PackageReference Include="Serilog" Version="3.1.1" />
    <PackageReference Include="Serilog.Sinks.Console" Version="5.0.1" />
    <PackageReference Include="Serilog.Sinks.File" Version="5.0.0" />
    <PackageReference Include="Serilog.Extensions.Hosting" Version="8.0.0" />

    <!-- WebSocket -->
    <PackageReference Include="System.Net.WebSockets.Client" Version="4.3.2" />

    <!-- JSON -->
    <PackageReference Include="System.Text.Json" Version="8.0.0" />
</ItemGroup>
```

---

## Next Steps for Agent

1. Create the solution structure as outlined above
2. Create database tables using provided SQL
3. Insert provider configurations
4. Implement parsers for each provider
5. Implement DerivClient for WebSocket communication
6. Implement BinaryExecutionService
7. Implement TelegramSignalScraperService
8. Add Telegram notifications
9. Test with each provider's signal format
10. Add trade result monitoring (optional enhancement)

{
  "ConnectionStrings": {
    "ConnectionString": "Server=108.181.161.170,51433;Database=bods;User Id=bdoadmin;Password=Seph$r0thes#;Encrypt=False;TrustServerCertificate=True"
  },
  "Deriv": {
    "AppId": "109082",
    "Token": "Z8EumYGYs4cyegf",
    "WebSocketUrl": "wss://ws.binaryws.com/websockets/v3?app_id=109082"
  },

---

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2025-01-04 | Auto-generated | Initial requirements document |


# DerivCTrader Automation - Project Context

**Last Updated:** 2026-01-20

This document is maintained by AI assistants to provide continuity across sessions when context windows fill up.

---

## Current State Summary

The project is a **trading automation system** that:
1. Scrapes trading signals from Telegram channels (DerivCTrader.SignalScraper)
2. Executes trades on cTrader platform (DerivCTrader.TradeExecutor)
3. Mirrors trades to Deriv binary options platform
4. Sends notifications via Telegram
5. Selective martingale execution (Dasha Trade) - executes only after provider loses
6. Scheduled binary signal execution (CMFLIX) - executes at pre-defined times
7. **NEW:** Deriv WebSocket connection resilience with auto-restart

### Recent Major Changes (Dec 2025 - Jan 2026)

#### Completed Jan 20, 2026 - Fixed Deriv Forex Symbol Mapping for Binary Options

**Summary:** Fixed incorrect forex symbol format causing "Trading is not offered for this asset" errors on Deriv.

**Problem:**
- Binary option placement failed with: `Deriv execution failed: Trading is not offered for this asset`
- Error occurred for forex pairs like EURUSD, GBPUSD, etc.
- Root cause: Asset mapper was using "frx" prefix format (e.g., "frxEURUSD") instead of slash format (e.g., "EUR/USD")

**Error Observed:**
```
2026-01-20 14:57:31 [ERR] Failed to process queue entry 15
System.Exception: Deriv execution failed: Trading is not offered for this asset.
   at DerivBinaryExecutorService.ProcessQueueEntryAsync
```

**File Modified:** [Derivmodels.cs](src/DerivCTrader.Infrastructure/Deriv/Derivmodels.cs)

**Fix:**
```csharp
// Before - Wrong format for binary options
["EURUSD"] = "frxEURUSD",  // ❌ This is for CFD/spot trading

// After - Correct format for binary options
["EURUSD"] = "EUR/USD",    // ✅ Binary options use slash format
```

**Changes Made:**
1. Updated all forex pair mappings to use slash format (EUR/USD, GBP/USD, etc.)
2. Added missing minor pairs from Deriv's available list (AUD/CAD, EUR/NZD, USD/MXN, USD/PLN, etc.)
3. Fixed fallback logic to format 6-letter pairs with slash: EURUSD → EUR/USD
4. Updated `IsForexPair()` to check for slash format instead of "frx" prefix
5. **Follow-up fix:** Added lowercase conversion for forex symbols (EUR/USD → eur/usd) in API calls
6. **Follow-up fix:** Enhanced error logging to capture full Deriv error details (code, message, details)

**Additional Fixes (Commit 1f0c528):**
- Deriv API expects lowercase forex symbols: `eur/usd` not `EUR/USD`
- Volatility indices remain uppercase: `R_10`, `1HZ100V`
- Added conditional formatting: forex pairs → lowercase, volatility indices → unchanged
- Enhanced error logging in proposal failures for better diagnostics

**Impact:** Binary options now execute successfully for all forex pairs supported by Deriv.

#### Completed Jan 20, 2026 - Fixed Deriv Auto-Reconnection Authorization Bug

**Summary:** Fixed critical bug where DerivClient auto-reconnection would not re-authorize, causing "Not authorized with Deriv" errors.

**Problem:**
- Binary option placement failed with: `System.InvalidOperationException: Not authorized with Deriv`
- Auto-reconnection logic successfully reconnected WebSocket but skipped re-authorization
- Root cause: Property name typo in line 468 - checking `_config.ApiToken` instead of `_config.Token`

**Error Observed:**
```
2026-01-20 00:00:04 [ERR] Error placing binary option
System.InvalidOperationException: Not authorized with Deriv
   at DerivCTrader.Infrastructure.Deriv.DerivClient.PlaceBinaryOptionAsync
```

**File Modified:** [Derivclient.cs:468](src/DerivCTrader.Infrastructure/Deriv/Derivclient.cs#L468)

**Fix:**
```csharp
// Before (line 468) - Wrong property name
if (!string.IsNullOrEmpty(_config.ApiToken))  // ❌ Property doesn't exist

// After - Correct property name
if (!string.IsNullOrEmpty(_config.Token))  // ✅ Matches DerivConfig.Token
```

**Root Cause Analysis:**
- DerivConfig class has property `Token` (not `ApiToken`)
- Condition was always false, so AuthorizeAsync was never called during auto-reconnection
- WebSocket would reconnect successfully but remain unauthorized
- PlaceBinaryOptionAsync checks `IsAuthorized` before making API calls → throws exception

**Impact:** Auto-reconnection now properly re-authorizes with Deriv API, preventing binary option placement failures after WebSocket disconnections.

#### In Progress Jan 19, 2026 - Asset Name Data Quality Issue

**Summary:** Investigating production error where Asset column contains "XAUUSD (Gold) Sell" instead of just "XAUUSD", causing cTrader symbol lookup failures.

**Error Observed:**
```
2026-01-19 17:36:00 [ERR] Failed to get symbol ID for XAUUSD (Gold) Sell
System.ArgumentException: Unknown symbol: XAUUSD (Gold) Sell
   at CTraderSymbolService.GetSymbolId(String assetName) line 398
   at CTraderOrderManager.GetSymbolId(String asset) line 1305
```

**Investigation Findings:**
- All current parsers (AFXGoldParser, GoldSignalParser1, GoldUnifiedParser) correctly set `Asset = "XAUUSD"`
- CTraderOrderManager, CTraderPendingOrderService, BinaryExecutionService all use signal.Asset directly without modification
- SqlServerTradeRepository.SaveParsedSignalAsync uses NormalizeAssetForStorage which only handles length truncation, not content changes
- Issue appears to be corrupted data in ParsedSignalsQueue table from old parser bug or manual entry

**Diagnostic Tools Created:**
- [diagnose-asset-issue.sql](diagnose-asset-issue.sql) - Query to find signals with malformed Asset values
- [fix-asset-names.sql](fix-asset-names.sql) - Cleanup script to fix Asset names by removing extra text

**Next Steps:**
1. Run diagnose-asset-issue.sql to identify affected records
2. Review results to confirm scope of issue
3. Run fix-asset-names.sql to clean up data
4. Consider adding validation to SaveParsedSignalAsync to reject malformed Asset values

#### Completed Jan 19, 2026 - DerivClient Auto-Reconnection on Aborted State

**Summary:** Added automatic reconnection logic to DerivClient when WebSocket enters Aborted or disconnected state during API operations.

**Problem Solved:**
- Production errors: `WebSocket is not ready for communication. State: Aborted`
- DerivClient would throw errors instead of attempting to reconnect
- Binary option placement failed when WebSocket was in bad state

**Solution:** Auto-reconnection in SendAndReceiveAsync

**File Modified:** [Derivclient.cs](src/DerivCTrader.Infrastructure/Deriv/Derivclient.cs)

**Changes:**
```csharp
// Before: Immediately threw error if WebSocket not Open
if (_webSocket == null || _webSocket.State != WebSocketState.Open)
{
    throw new InvalidOperationException(...);
}

// After: Auto-reconnect with 3 retries
if (_webSocket == null || _webSocket.State != WebSocketState.Open)
{
    _logger.LogWarning("WebSocket not ready. Attempting reconnection...");

    for (int attempt = 1; attempt <= 3; attempt++)
    {
        await ConnectAsync(cancellationToken);
        await AuthorizeAsync(cancellationToken); // Re-authorize after reconnect
        // ... retry logic with exponential backoff
    }
}
```

**Behavior:**
1. Before each API call, check WebSocket state
2. If not Open (Closed, Aborted, etc.), attempt reconnection (max 3 tries)
3. Re-authorize after successful reconnection
4. Exponential backoff: 2s, 4s, 6s between attempts
5. Only throw error after 3 failed attempts

**Benefits:**
- Automatic recovery from transient connection issues
- Reduced binary option placement failures
- Better resilience in production environment

---

#### Completed Jan 09, 2026 - Deriv WebSocket Connection Resilience

**Summary:** Implemented robust connection handling for Deriv WebSocket client with automatic reconnection tracking, heartbeat keep-alive, and service auto-restart after repeated failures.

**Key Features:**
- **Reconnection Counter:** Tracks failed reconnection attempts (max 5)
- **Stability Reset:** Counter resets to 0 after 30 minutes of stable connection
- **Heartbeat Mechanism:** Sends ping every 30 seconds to keep connection alive
- **Auto-Restart:** Triggers `Environment.Exit(1)` after max failures for service restart

**File Modified:** [DerivWebSocketClient.cs](src/DerivCTrader.Infrastructure/Trading/DerivWebSocketClient.cs)

**New Constants:**
```csharp
private const int MAX_RECONNECT_ATTEMPTS = 5;
private const int STABILITY_RESET_MINUTES = 30;
private const int HEARTBEAT_INTERVAL_SECONDS = 30;
```

**New Methods:**
- `HandleReconnection()` - Tracks reconnection events and triggers restart if max reached
- `HandleConnectionFailure()` - Increments counter on connection failures
- `CheckAndResetReconnectCounter()` - Resets counter after 30 minutes of stability
- `StartHeartbeat()` / `StopHeartbeat()` - Manages ping timer
- `TriggerServiceRestart()` - Calls `Environment.Exit(1)` for process restart

**Behavior:**
```
Connection fails → Increment counter → Log warning
Counter reaches 5 → Log critical → Environment.Exit(1)
Connection stable for 30 min → Reset counter to 0
Every 30 seconds → Send ping to keep alive
```

---

#### Completed Jan 09, 2026 - Telegram Session File Auto-Deployment

**Summary:** Enhanced Azure DevOps pipeline to automatically include Telegram session files in deployments, eliminating manual file copying to VPS.

**Problem Solved:**
- Previously, session files required manual copying to VPS after each re-authentication
- Session loss required re-authenticating with Telegram verification code on VPS

**Solution: Azure DevOps Secure Files**

**Files Modified:**

| File | Changes |
|------|---------|
| [azure-pipelines-deploy.yml](azure-pipelines-deploy.yml) | Added DownloadSecureFile task + copy session to artifact |
| [DEPLOYMENT.md](DEPLOYMENT.md) | Updated documentation for Secure Files approach |

**Pipeline Changes (azure-pipelines-deploy.yml):**
```yaml
# Download session file from Secure Files
- task: DownloadSecureFile@1
  displayName: 'Download Telegram Session File'
  name: TelegramSession
  inputs:
    secureFile: 'WTelegram.session'

# Copy to artifact
- task: PowerShell@2
  displayName: 'Copy Session File to Artifact'
  inputs:
    targetType: inline
    script: |
      Copy-Item "$(TelegramSession.secureFilePath)" "$(Build.ArtifactStagingDirectory)\signalscraper\WTelegram.session"
```

**How It Works:**
1. Session file stored in Azure DevOps Secure Files (encrypted)
2. Pipeline downloads session file during build stage
3. Session file included in SignalScraper artifact
4. Deployed to VPS with each release
5. VPS backup (`session_backup\`) preserved as fallback

**First-Time Setup Required:**
1. Go to Azure DevOps → Pipelines → Library → Secure files
2. Upload `WTelegram.session` from: `src\DerivCTrader.SignalScraper\bin\WTelegram.session`
3. Check "Authorize for use in all pipelines"

**Benefits:**
- No manual file copying after re-authentication
- Single source of truth in Azure DevOps
- Automatic deployment with every release
- Encrypted storage for sensitive session data

---

#### Completed Jan 08, 2026 - CMFLIX Gold Signals (Scheduled Binary Execution)

**Summary:** Implemented a scheduled binary signal provider that parses batch signals with future execution times, converts Brazilian time (UTC-3) to UTC, and executes trades at the exact scheduled time.

**Signal Format:**
```
CMFLIX GOLD SIGNALS
🗓️ 08/01
🕓 5 MINUTOS

• 09:10 - EUR/GBP - CALL
• 09:45 - EUR/USD - CALL
• 10:00 - USD/CAD - PUT
...
```

**Key Features:**
- Batch parsing: One message contains multiple signals for the day
- Time conversion: Brazilian time (UTC-3) → UTC (add 3 hours)
- Scheduled execution: Waits until exact ScheduledAtUtc time before executing
- Chunked waiting: 10-second intervals for responsive cancellation
- Catch-up logic: Processes due signals that may have been missed (up to 5 minutes late)
- 15-minute expiry for all trades

**New Files Created:**

| File | Purpose |
|------|---------|
| [Database/AddScheduledAtUtcColumn.sql](Database/AddScheduledAtUtcColumn.sql) | Migration for ScheduledAtUtc column |
| [Database/BinaryBacktestSetup.sql](Database/BinaryBacktestSetup.sql) | Backtesting tables (BacktestCandles, BinaryBacktestRuns, BinaryBacktestTrades) |
| [CmflixParser.cs](src/DerivCTrader.Application/Parsers/CmflixParser.cs) | Batch parser with Brazilian time conversion |
| [ScheduledBinaryExecutionService.cs](src/DerivCTrader.TradeExecutor/Services/ScheduledBinaryExecutionService.cs) | Time-based execution background service |

**Files Modified:**

| File | Changes |
|------|---------|
| [ParsedSignal.cs](src/DerivCTrader.Domain/Entities/ParsedSignal.cs) | Added `ScheduledAtUtc` property |
| [ITradeRepository.cs](src/DerivCTrader.Application/Interfaces/ITradeRepository.cs) | Added `GetNextScheduledSignalAsync()`, `GetScheduledSignalsDueAsync()` |
| [SqlServerTradeRepository.cs](src/DerivCTrader.Infrastructure/Persistence/SqlServerTradeRepository.cs) | Implemented scheduled queries, updated INSERT for ScheduledAtUtc |
| [TelegramSignalScraperService.cs](src/DerivCTrader.SignalScraper/Services/TelegramSignalScraperService.cs) | Added CMFLIX channel routing |
| [DerivWebSocketClient.cs](src/DerivCTrader.Infrastructure/Trading/DerivWebSocketClient.cs) | Added `GetSpotPriceAsync()` method |
| [Program.cs](src/DerivCTrader.SignalScraper/Program.cs) | Registered CmflixParser |
| [Program.cs](src/DerivCTrader.TradeExecutor/Program.cs) | Registered ScheduledBinaryExecutionService |
| appsettings.Production.json (both projects) | Added CMFLIX channel and config |

**Signal Flow:**
```
Telegram batch message "CMFLIX GOLD SIGNALS..."
    │
    ▼
SignalScraper → CmflixParser.ParseBatch() → List<ParsedSignal>
    │
    ▼
For each signal: SaveParsedSignalAsync() with ScheduledAtUtc = Brazilian time + 3 hours
    │
    ▼
TradeExecutor → ScheduledBinaryExecutionService
    │
    ├─ GetNextScheduledSignalAsync() → Find next signal
    ├─ Wait in 10-second chunks until ScheduledAtUtc
    └─ PlaceBinaryOptionAsync() → Execute on Deriv
           │
           ▼
    MarkSignalAsProcessedAsync() → Send Telegram notification
```

**Configuration (appsettings.Production.json):**
```json
"ProviderChannels": {
  "CMFLIX": "-1001473818334"
},
"Cmflix": {
  "ChannelId": "-1001473818334",
  "StakeUsd": 20,
  "ExpiryMinutes": 15,
  "Enabled": true
}
```

**Database Migration Required:** Run `Database/AddScheduledAtUtcColumn.sql`

---

#### Completed Jan 06, 2026 - Dasha Trade Selective Martingale System

**Summary:** Implemented a selective martingale interception system that monitors Telegram signals, waits for expiry, and ONLY executes on Deriv if the provider's signal lost. Uses positive compounding ($50 → $100 → $200 → reset).

**Strategy Logic:**
- Signal received: "USDJPY m15 down"
- System captures entry price at signal receipt
- Waits until expiry time (e.g., 15 minutes)
- Captures exit price at expiry
- Determines if provider lost:
  - DOWN signal lost if Exit > Entry
  - UP signal lost if Exit < Entry
- If provider WON → Ignore (no trade)
- If provider LOST → Execute same direction on Deriv with compounding stake

**Compounding State Machine:**
```
STEP 0: $50  ──WIN──▶  STEP 1: $100  ──WIN──▶  STEP 2: $200  ──ANY──▶  RESET
    │                      │                        │
    └───────LOSS───────────┴──────────LOSS──────────┴─────────▶  RESET ($50)
```

**New Files Created:**

| File | Purpose |
|------|---------|
| [Database/DashaTradeSetup.sql](Database/DashaTradeSetup.sql) | 4 tables + initial config |
| [DashaPendingSignal.cs](src/DerivCTrader.Domain/Entities/DashaPendingSignal.cs) | Signal awaiting expiry evaluation |
| [DashaProviderConfig.cs](src/DerivCTrader.Domain/Entities/DashaProviderConfig.cs) | Per-provider martingale settings |
| [DashaCompoundingState.cs](src/DerivCTrader.Domain/Entities/DashaCompoundingState.cs) | Persistent compounding ladder state |
| [DashaTrade.cs](src/DerivCTrader.Domain/Entities/DashaTrade.cs) | Executed trade records |
| [IDashaTradeRepository.cs](src/DerivCTrader.Application/Interfaces/IDashaTradeRepository.cs) | Repository interface |
| [SqlServerDashaTradeRepository.cs](src/DerivCTrader.Infrastructure/Persistence/SqlServerDashaTradeRepository.cs) | Dapper-based implementation |
| [DashaTradeParser.cs](src/DerivCTrader.Application/Parsers/DashaTradeParser.cs) | Parses "USDJPY m15 down" format |
| [DashaCompoundingManager.cs](src/DerivCTrader.TradeExecutor/Services/DashaCompoundingManager.cs) | Stake state machine logic |
| [DashaTradeExecutionService.cs](src/DerivCTrader.TradeExecutor/Services/DashaTradeExecutionService.cs) | Main background execution service |

**Files Modified:**

| File | Changes |
|------|---------|
| [IDerivClient.cs](src/DerivCTrader.Application/Interfaces/IDerivClient.cs) | Added `GetSpotPriceAsync()` interface |
| [DerivClient.cs](src/DerivCTrader.Infrastructure/Deriv/DerivClient.cs) | Implemented spot price via `ticks_history` API |
| [TelegramSignalScraperService.cs](src/DerivCTrader.SignalScraper/Services/TelegramSignalScraperService.cs) | Routes Dasha signals to pending table |
| [Program.cs](src/DerivCTrader.SignalScraper/Program.cs) | Registered DashaTradeParser + repository |
| [Program.cs](src/DerivCTrader.TradeExecutor/Program.cs) | Registered execution service + manager |
| [appsettings.Production.json](src/DerivCTrader.SignalScraper/appsettings.Production.json) | Added DashaTrade channel ID |

**Database Tables Created:**
- `DashaPendingSignals` - Signals awaiting expiry evaluation
- `DashaProviderConfig` - Per-provider configuration (extensible)
- `DashaCompoundingState` - Persistent compounding state per provider
- `DashaTrades` - Executed trade records with outcomes

**Signal Flow:**
```
Telegram "USDJPY m15 down"
    │
    ▼
SignalScraper → DashaPendingSignals (EntryPrice=0, Status=AwaitingExpiry)
    │
    ▼
TradeExecutor fills entry price via Deriv Tick API (ticks_history)
    │
    ▼
[Wait until ExpiryAt <= Now]
    │
    ▼
Fetch exit price → Evaluate provider outcome
    │
    ├─ Provider Won → Update status, no trade
    └─ Provider Lost → Execute on Deriv (Rise/Fall) with compounding stake
           │
           ▼
    Monitor contract settlement → Update compounding state
```

**Key Technical Decisions:**
- **Price Source:** Deriv Tick API (`ticks_history` with `count: 1`) - free, uses same prices Deriv settles on
- **Two-Phase Price Capture:** SignalScraper saves with EntryPrice=0, TradeExecutor fills it quickly
- **Database-Driven Config:** Extensible for adding other providers with different settings

**Deployment Requirement:** Run `Database/DashaTradeSetup.sql` before starting services.

---

#### Completed Jan 02, 2026 - Provider Compatibility Fixes (DerivPlus / AFXGold / All Telegram Providers)

**Summary:** Fixed end-to-end execution for per-signal PureBinary expiry (e.g., DerivPlus “Expiry: 5 Minutes”) and hardened the Telegram → DB → TradeExecutor pipeline against real-world ID formats and DB schema drift. Also reduced log spam from unrelated Telegram updates.

**Why this mattered (real incidents fixed):**
- Some provider channel IDs in config/DB are stored as `-100...` (supergroup) while WTelegram exposes `channel_id` as a positive integer (e.g., `1628868943`). This caused provider lookup/parsers to miss signals and log “NOT A CONFIGURED CHANNEL”.
- Some deployments have `ParsedSignalsQueue.Timeframe` as `INT`, but the domain model uses `string?`. This caused inserts to fail (e.g., trying to store `"5M"` into an `INT`) and later caused TradeExecutor to fall back to default expiry (30m) because Dapper didn’t populate `Timeframe`.

**Changes Made (shared pipeline, impacts DerivPlus + AFXGold + any Telegram-based provider):**

1. **SignalScraper: Channel ID normalization + quieter unknown-channel logging**
   - Normalized numeric ID mapping so both config formats work:
     - `-1001234567890` → mapping key `1234567890`
     - `-1628868943` → mapping key `1628868943`
   - Reduced spam logs for unknown channels: warn once per channel, repeats are downgraded.
   - File: [src/DerivCTrader.SignalScraper/Services/TelegramSignalScraperService.cs](src/DerivCTrader.SignalScraper/Services/TelegramSignalScraperService.cs)

2. **DerivPlus parser: accept both channel formats; encode expiry in Timeframe**
   - `CanParse` accepts both `-1628868943` and `-1001628868943`.
   - Extracts expiry from messages like `Expiry: 5 Minutes` and stores as `Timeframe = "5M"` for the PureBinary executor.
   - File: [src/DerivCTrader.Application/Parsers/DerivPlusParser.cs](src/DerivCTrader.Application/Parsers/DerivPlusParser.cs)

3. **Repository: make Timeframe schema-drift tolerant (INT vs NVARCHAR)**
   - On insert (`SaveParsedSignalAsync`): detects whether `ParsedSignalsQueue.Timeframe` is `INT` and, if so, converts minute-based strings like `5M` → `5` (or stores `NULL` if not minute-based).
   - On read (`GetUnprocessedSignalsAsync`): selects explicit columns and casts `Timeframe` to NVARCHAR so Dapper reliably populates `ParsedSignal.Timeframe`.
   - File: [src/DerivCTrader.Infrastructure/Persistence/SqlServerTradeRepository.cs](src/DerivCTrader.Infrastructure/Persistence/SqlServerTradeRepository.cs)

4. **TradeExecutor PureBinary: expiry parsing now supports numeric Timeframe**
   - Expiry parser accepts:
     - `"5"` (numeric minutes, typical when DB column is `INT`)
     - `"5M"`, `"5 Min"`, `"5 Minutes"`
   - Prevents fallback to 30 minutes when `Timeframe` is present.
   - File: [src/DerivCTrader.TradeExecutor/Services/BinaryExecutionService.cs](src/DerivCTrader.TradeExecutor/Services/BinaryExecutionService.cs)

**Provider impact notes:**
- **DerivPlus:** PureBinary signals now execute with the per-signal expiry end-to-end (confirmed in runtime logs). Channel ID format no longer blocks parsing.
- **AFXGold:** Benefits from the same channel ID normalization and DB Timeframe tolerance (even if AFXGold itself uses provider-specific expiry elsewhere, it’s no longer blocked by ID mismatch issues).
- **Massive:** No functional changes were made to the Massive API/backtesting components; the above changes are focused on Telegram providers and the parsed-signal queue pipeline.

**Operational note:** These changes require restarting both `DerivCTrader.SignalScraper` and `DerivCTrader.TradeExecutor` to take effect.

#### Completed Dec 16, 2025 - Telegram Message Threading

**Summary:** Implemented reply threading for Telegram notifications to organize trade lifecycle messages.

**Changes Made:**

1. **Database Schema Updates**
   - Added `TelegramMessageId INT NULL` to `ParsedSignalsQueue` - stores original Telegram message ID from provider
   - Added `NotificationMessageId INT NULL` to `ParsedSignalsQueue` - reserved for future order creation notifications
   - Added `TelegramMessageId INT NULL` to `ForexTrades` - stores fill notification message ID for threading
   - **Migration Required:** Run `Database/AddTelegramMessageIdColumns.sql`

2. **Enhanced TelegramNotifier**
   - Added new method: `Task<int?> SendTradeMessageAsync(string message, int replyToMessageId, CancellationToken cancellationToken)`
   - Returns message_id from Telegram API for future threading
   - Uses `reply_to_message_id` parameter to create threaded conversations
   - Graceful fallback to standalone messages if reply fails

3. **Signal Scraper Updates**
   - `TelegramSignalScraperService.cs` now captures `message.id` from incoming Telegram signals
   - Stores in `ParsedSignal.TelegramMessageId` for audit trail
   - No notification sent for received signals (raw provider data)

4. **CTraderPendingOrderService Updates**
   - **Fill Notifications:** Simplified format - `?? GBPUSD Sell @ 1.27500, TP: 1.26500, SL: 1.28000` (removed "ORDER FILLED" header)
   - **SL/TP Modification Notifications:** 
     - **Pending orders** (not filled): Silent database update, NO notification
     - **Active positions** (filled): Sends notification as reply to fill message
     - Format: `?? SL/TP Modified: GBPUSD Sell - SL: 1.27300, TP: 1.26000`
   - **Close Notifications:** Reply to fill notification showing final result

5. **Repository Updates**
   - `SaveParsedSignalAsync()` - Added TelegramMessageId and NotificationMessageId columns
   - `CreateForexTradeAsync()` - Added TelegramMessageId column
   - `UpdateParsedSignalNotificationMessageIdAsync()` - New method for updating notification message ID

**Notification Flow:**
```
? Signal received ? No notification (raw provider signal)
? Order created ? No notification (by design)
? Order filled ? Notification sent (starts thread)
? SL/TP modified (pending) ? No notification (not filled yet)
? SL/TP modified (filled) ? Notification sent (reply to fill)
? Position closed ? Notification sent (reply to fill)
```

**Telegram Thread Example:**
```
?? GBPUSD Sell @ 1.27500, TP: 1.26500, SL: 1.28000
  ?? ?? SL/TP Modified: GBPUSD Sell - SL: 1.27300, TP: 1.26000
  ?? ?? CLOSED @ 1.260 - ? Profit (TP Hit) - R:R=1:3
```

**Files Modified:**
- `src/DerivCTrader.Domain/Entities/ParsedSignal.cs` - Added TelegramMessageId, NotificationMessageId
- `src/DerivCTrader.Domain/Entities/ForexTrade.cs` - Added TelegramMessageId
- `src/DerivCTrader.Application/Interfaces/ITelegramNotifier.cs` - Added reply method
- `src/DerivCTrader.Application/Interfaces/ITradeRepository.cs` - Added UpdateParsedSignalNotificationMessageIdAsync
- `src/DerivCTrader.Infrastructure/Notifications/TelegramNotifier.cs` - Implemented reply threading
- `src/DerivCTrader.Infrastructure/CTrader/CTraderPendingOrderService.cs` - Updated all notification methods
- `src/DerivCTrader.SignalScraper/Services/TelegramSignalScraperService.cs` - Capture message IDs
- `src/DerivCTrader.Infrastructure/Persistence/SqlServerTradeRepository.cs` - Updated INSERT/UPDATE queries

**Documentation Created:**
- `TELEGRAM_MESSAGE_THREADING.md` - Complete implementation guide
- `FINAL_TELEGRAM_UPDATE.md` - Summary of final changes
- `Database/AddTelegramMessageIdColumns.sql` - Migration script

#### Completed Dec 15, 2025

1. **PnL Extraction Fix**
   - Fixed `TryExtractProfit()` to use `GrossProfit`/`Profit` instead of `Balance`
   - Balance was showing account balance (e.g., 4056.17) instead of actual trade P&L
   - File: `src/DerivCTrader.Infrastructure/CTrader/CTraderPendingOrderService.cs`

2. **Outcome Field Added**
   - Added `Outcome` property to `ForexTrade` entity ("Profit", "Loss", "Breakeven")
   - Added `DetermineOutcome()` helper method
   - Updated SQL INSERT/UPDATE in `SqlServerTradeRepository.cs` to include Outcome
   - **Database Migration Required:** `ALTER TABLE ForexTrades ADD Outcome NVARCHAR(20) NULL;`

3. **Telegram Close Message Format**
   - Updated `FormatCloseMessage()` to show "BE" instead of "Break-even"
   - Format: `CLOSED {symbol} {direction} @ {exitPrice}` + `{emoji} PnL={status} Reason={reason}`

4. **SL/TP Modification Event Handling**
   - Added `IsSlTpModificationEvent()` to detect modification events
   - Added `HandleSlTpModificationAsync()` to process SL/TP changes for both:
     - **Pending Orders**: Updates `ParsedSignalsQueue` table
     - **Active Positions**: Updates `ForexTrades` table SL/TP columns
   - Added `TryExtractStopLoss()` and `TryExtractTakeProfit()` helper methods
   - Added `UpdateParsedSignalSlTpAsync()` to repository for updating queue SL/TP

5. **SL/TP Columns Added to ForexTrades**
   - Added `SL` and `TP` properties to `ForexTrade` entity
   - Updated SQL INSERT/UPDATE in `SqlServerTradeRepository.cs` to include SL/TP
   - Updated `PersistForexTradeFillAsync` to save initial SL/TP from signal
   - Updated `HandleSlTpModificationAsync` to update trade.SL and trade.TP columns
   - **Database Migration Already Applied by User**

6. **Close Reason Inference Fix**
   - Updated `InferCloseReason()` to compare exit price against SL/TP values
   - Uses 0.5% tolerance for price slippage
   - Now correctly identifies "SL Hit" when exit price matches SL (was showing "Closed Manually")

7. **Risk:Reward (R:R) Calculation**
   - Added `CalculateRiskReward()` method to calculate R:R at trade close
   - Format: "1:X" where X is reward per unit of risk (e.g., "1:2", "1:3")
   - Inverted format "X:1" when risk > reward (e.g., "5:1")
   - Added `RR` property to `ForexTrade` entity
   - Uses **modified SL/TP values at close time**, not original signal values
   - **Database Migration Required:** `ALTER TABLE ForexTrades ADD RR NVARCHAR(10) NULL;`

#### Previously Completed (From Earlier Sessions)

1. **Signal Processing Pipeline Fixed**
   - Fixed `DerivWebSocketClient` tick subscription bug
   - Restored core logic in `HandlePendingExecutionAsync` (GPT-4.1 had removed it)
   - Fixed `OrderId` type handling (long, not string)

2. **Position/Order Tracking**
   - Added `PositionId` column to `ForexTrades` table
   - Added `Strategy` column (stores provider name)
   - Changed Queue table to store `PositionId` instead of `OrderId`
   - Added `TryHandlePositionCloseByFillAsync()` for manual close detection

3. **Position Close Detection**
   - Added `IsPositionClosedExecution()` to detect various close event types
   - Added `HandlePositionClosedAsync()` for updating ForexTrades on close
   - Fixed `TryExtractExitPrice()` with proper scaling for cTrader prices

---

## Key Files & Their Purposes

### Dasha Trade Selective Martingale
- **`src/DerivCTrader.TradeExecutor/Services/DashaTradeExecutionService.cs`** - Main background service
  - `FillEntryPricesAsync()` - Captures entry prices for new signals via Deriv Tick API
  - `ProcessPendingSignalsAsync()` - Evaluates signals at expiry time
  - `EvaluateAndExecuteSignalAsync()` - Determines provider outcome, executes if lost
  - `ProcessUnsettledTradesAsync()` - Monitors contract settlements

- **`src/DerivCTrader.TradeExecutor/Services/DashaCompoundingManager.cs`** - Stake state machine
  - `GetCurrentStakeAsync()` - Returns current stake based on ladder step
  - `RecordTradeResultAsync()` - Updates state after trade settlement
  - `ResetStateAsync()` - Resets to initial stake

- **`src/DerivCTrader.Application/Parsers/DashaTradeParser.cs`** - Signal parser
  - Regex: `^([A-Z]{6,7})\s+([mMhHdD]\d+)\s+(up|down)$`
  - Channel ID: `-1001570351142`

- **`src/DerivCTrader.Domain/Entities/DashaPendingSignal.cs`** - Signal entity
  - `DidProviderLose()` - Determines if provider lost based on entry/exit prices
  - Status: AwaitingExpiry → ProviderWon/ProviderLost/Executed/Error

- **`src/DerivCTrader.Infrastructure/Persistence/SqlServerDashaTradeRepository.cs`** - Repository
  - `GetSignalsNeedingEntryPriceAsync()` - Gets signals with EntryPrice=0
  - `GetSignalsAwaitingEvaluationAsync()` - Gets expired signals ready for evaluation

### Core Signal Processing
- **`src/DerivCTrader.Infrastructure/CTrader/CTraderPendingOrderService.cs`** - Main order tracking service
  - `OnClientMessageReceived()` - Entry point for all cTrader execution events
  - `HandlePendingExecutionAsync()` - Processes order fills
  - `HandlePositionClosedAsync()` - Processes position closes (TP/SL/manual)
  - `TryHandlePositionCloseByFillAsync()` - Handles closes for unknown orders
  - `HandleSlTpModificationAsync()` - Handles SL/TP modifications (notifies for filled orders only)
  - `NotifyFillAsync()` - Sends fill notification (simplified format, stores message_id)

### Telegram Integration
- **`src/DerivCTrader.Infrastructure/Notifications/TelegramNotifier.cs`** - Telegram notification service
  - `SendTradeMessageAsync(string message)` - Send standalone message
  - `SendTradeMessageAsync(string message, int replyToMessageId)` - Send threaded reply
  - Returns message_id for future threading

- **`src/DerivCTrader.SignalScraper/Services/TelegramSignalScraperService.cs`** - Signal scraper
  - `HandleUpdateAsync()` - Captures Telegram message IDs from incoming signals
  - Stores message_id in `ParsedSignal.TelegramMessageId`

### Data Models
- **`src/DerivCTrader.Domain/Entities/ForexTrade.cs`** - Trade entity
  - `PositionId` - cTrader position ID
  - `Strategy` - Provider/strategy name
  - `Outcome` - "Profit", "Loss", "Breakeven"
  - `RR` - Risk:Reward ratio (e.g., "1:2", "5:1")
  - `SL` - Stop Loss price (updated on fill and modifications)
  - `TP` - Take Profit price (updated on fill and modifications)
  - `TelegramMessageId` - Fill notification message ID for threading replies

- **`src/DerivCTrader.Domain/Entities/ParsedSignal.cs`** - Signal entity
  - `TelegramMessageId` - Original message ID from provider channel
  - `NotificationMessageId` - Message ID of notification sent (for threading)

### Persistence
- **`src/DerivCTrader.Infrastructure/Persistence/SqlServerTradeRepository.cs`** - Database operations
  - `CreateForexTradeAsync()` - Insert with Outcome, SL, TP, RR, TelegramMessageId columns
  - `UpdateForexTradeAsync()` - Update with Outcome, SL, TP, RR columns
  - `FindLatestForexTradeByCTraderPositionIdAsync()` - Find trade by PositionId
  - `UpdateParsedSignalSlTpAsync()` - Update SL/TP in ParsedSignalsQueue
  - `SaveParsedSignalAsync()` - Save signal with TelegramMessageId, NotificationMessageId
  - `UpdateParsedSignalNotificationMessageIdAsync()` - Update notification message ID

---

## Technical Details

### cTrader Price Scaling
- Prices stored as long integers (e.g., 2447500000 = 2447.5)
- Divide by 100000 for 5 decimal places
- Money values (PnL) stored in cents - divide by 100

### Execution Event Flow
1. `OnClientMessageReceived()` receives `ProtoOAExecutionEvent`
2. Check for position close -> `HandlePositionClosedAsync()`
3. Check for SL/TP modification -> `HandleSlTpModificationAsync()`
4. Check for order fill -> `HandlePendingExecutionAsync()` or `TryHandlePositionCloseByFillAsync()`

### Telegram Message Formats
```
Fill (simplified):
?? GBPUSD Sell @ 1.27500, TP: 1.26500, SL: 1.28000

SL/TP Modified (filled orders only):
?? SL/TP Modified: GBPUSD Sell
SL: 1.27300, TP: 1.26000

Close:
?? CLOSED GBPUSD Sell @ 1.260
? PnL=Profit Reason=TP Hit
?? R:R=1:3
```

### Telegram Threading
- Fill notification is the **first message** (no reply) - message_id stored in `ForexTrade.TelegramMessageId`
- SL/TP modification notifications **reply to fill** using stored message_id
- Close notifications **reply to fill** using stored message_id
- Creates organized conversation threads in Telegram chat

---

## Pending Tasks / Known Issues

### Database Migrations Required
```sql
-- Jan 06, 2026 - Dasha Trade Selective Martingale (RUN FULL SCRIPT)
-- Run Database/DashaTradeSetup.sql which creates:
--   DashaPendingSignals, DashaProviderConfig, DashaCompoundingState, DashaTrades
-- Plus initial config insert for DashaTrade provider

-- Dec 16, 2025 - Telegram Threading (NOT YET APPLIED)
ALTER TABLE ParsedSignalsQueue ADD TelegramMessageId INT NULL;
ALTER TABLE ParsedSignalsQueue ADD NotificationMessageId INT NULL;
ALTER TABLE ForexTrades ADD TelegramMessageId INT NULL;

-- Dec 15, 2025 - Already Applied by User
ALTER TABLE ForexTrades ADD Outcome NVARCHAR(20) NULL;
ALTER TABLE ForexTrades ADD SL DECIMAL(18,8) NULL;
ALTER TABLE ForexTrades ADD TP DECIMAL(18,8) NULL;
ALTER TABLE ForexTrades ADD RR NVARCHAR(10) NULL;
```

### Potential Improvements
1. Optional: Enable order creation notifications (currently disabled by design)
2. Add more detailed logging for debugging execution events
3. Consider adding trade statistics/metrics tracking
4. Add historical SL/TP change tracking (audit trail beyond Notes field)
5. **Dasha Trade:** Add web dashboard for viewing provider stats and compounding state
6. **Dasha Trade:** Add manual compounding reset endpoint/command
7. **Dasha Trade:** Add configurable cooldown between executions

---

## Build & Deployment Notes

- Solution: `DerivCTraderAutomation.sln`
- .NET 8.0
- Running services may lock DLLs - use PowerShell to kill processes before rebuilding:
  ```powershell
  Get-Process -Name 'DerivCTrader*' -ErrorAction SilentlyContinue | Stop-Process -Force
  ```

### Deployment Model

**Both applications run as Console Applications (not Windows Services or Scheduled Tasks)**

| Project | Type | SDK |
|---------|------|-----|
| DerivCTrader.SignalScraper | Console App (Hosted Service) | Microsoft.NET.Sdk |
| DerivCTrader.TradeExecutor | Console App (Multi-Service Host) | Microsoft.NET.Sdk |

**Key Characteristics:**
- Use .NET Generic Host pattern with `Host.CreateDefaultBuilder()` and `host.RunAsync()`
- Run continuously via `BackgroundService` classes
- Managed via PowerShell scripts (`restart-services.ps1`)
- No `UseWindowsService()` - not registered as Windows Services
- No scheduled task integration

**To Convert to Windows Services (future):**
1. Add NuGet package: `Microsoft.Extensions.Hosting.WindowsServices`
2. Add `.UseWindowsService()` before `.Build()` in Program.cs
3. Install using `sc create` or similar

---

## Contact Points in Code

For Dasha Trade selective martingale issues, check:
- `DashaTradeExecutionService.ExecuteAsync()` - Main loop (fill prices → evaluate → check outcomes)
- `DashaTradeExecutionService.EvaluateAndExecuteSignalAsync()` - Provider outcome logic
- `DashaCompoundingManager.RecordTradeResultAsync()` - Stake state machine
- `DashaPendingSignal.DidProviderLose()` - Win/loss determination
- `TelegramSignalScraperService.SaveDashaTradePendingSignalAsync()` - Signal routing

For signal processing issues, start at:
- `CTraderPendingOrderService.OnClientMessageReceived()` (line ~293)

For Telegram threading issues, check:
- `TelegramNotifier.SendTradeMessageAsync()` - Reply threading implementation
- `CTraderPendingOrderService.NotifyFillAsync()` - Fill notification (captures message_id)
- `CTraderPendingOrderService.HandleSlTpModificationAsync()` - SL/TP threading
- `CTraderPendingOrderService.HandlePositionClosedAsync()` - Close threading

For database issues, check:
- `SqlServerTradeRepository` methods
- Connection string in `appsettings.json`

For Telegram notifications:
- `ITelegramNotifier.SendTradeMessageAsync()`
- `TelegramNotifier` implementation

---

## Quick Reference - Notification Logic

**What Gets Notified:**
- ? Order filled (starts thread)
- ? SL/TP modified on **filled orders** (replies to fill)
- ? Position closed (replies to fill)

**What Does NOT Get Notified:**
- ? Signal received (raw provider data)
- ? Order created (optional - disabled by design)
- ? SL/TP modified on **pending orders** (not filled yet)

**Rationale:** Minimize spam while providing complete lifecycle visibility for active trades.

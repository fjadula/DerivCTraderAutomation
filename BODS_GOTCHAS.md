# BODS Implementation Gotchas & Critical Notes

This document highlights critical implementation details, edge cases, and gotchas that the implementing agent MUST be aware of.

---

## 🚨 CRITICAL GOTCHAS

### 1. Telegram Channel ID Formats

**Problem:** Telegram channel IDs come in different formats that must ALL be handled.

```
Supergroup format: -1001641563130 (prefix -100)
Regular format:    -1641563130    (prefix -)
Raw ID only:       1641563130     (no prefix)
```

**Solution:**
```csharp
private long NormalizeChannelId(long rawId)
{
    var idStr = rawId.ToString();

    // Handle supergroup (-100 prefix)
    if (idStr.StartsWith("-100"))
        return long.Parse(idStr.Substring(4));

    // Handle regular chat/channel (- prefix)
    if (idStr.StartsWith("-"))
        return long.Parse(idStr.Substring(1));

    return rawId;
}
```

**ALSO:** Store ALL formats in the lookup dictionary:
```csharp
_channelMap["-1001641563130"] = config;
_channelMap["-1641563130"] = config;
_channelMap["1641563130"] = config;
```

---

### 2. FXPIPSPREDATOR Has NO Asset in Message

**Problem:** This provider's signals don't include the asset name:
```
BUY___4388-4381
SL___4375
TP___4397
```

**Solutions (pick one):**
1. **Hardcode XAUUSD** - This channel only trades gold
2. **Use last known asset** - Track state per channel
3. **Make it configurable** - Add `DefaultAsset` to ProviderChannelConfig

**Recommendation:** Add `DefaultAsset` column to ProviderChannelConfig:
```sql
ALTER TABLE ProviderChannelConfig ADD DefaultAsset NVARCHAR(20) NULL;
UPDATE ProviderChannelConfig SET DefaultAsset = 'XAUUSD' WHERE ProviderName = 'FXPIPSPREDATOR';
```

---

### 3. Boom/Crash Assets Don't Support Rise/Fall

**Problem:** Boom and Crash indices on Deriv only support specific contract types (Boom/Crash contracts), NOT Rise/Fall.

**MUST FILTER THESE OUT:**
```csharp
private bool IsRiseFallSupported(string asset)
{
    var upperAsset = asset.ToUpperInvariant();
    return !upperAsset.Contains("BOOM") && !upperAsset.Contains("CRASH");
}

// In BinaryExecutionService:
if (!IsRiseFallSupported(signal.Asset))
{
    _logger.LogWarning("Skipping {Asset} - Rise/Fall not supported for Boom/Crash", signal.Asset);
    await _repository.MarkSignalAsProcessedAsync(signal.SignalId);
    continue;
}
```

---

### 4. Deriv Proposal Can Expire

**Problem:** When you request a proposal from Deriv, it has a limited validity window (~30 seconds).

**Flow:**
1. Request proposal → Get proposal ID + ask price
2. Buy contract with proposal ID
3. **If too slow, proposal expires and buy fails**

**Solution:**
```csharp
// Get proposal
var proposal = await GetProposalAsync(asset, direction, stake, expiry);

// Immediately buy - don't delay!
var buyResult = await BuyContractAsync(proposal.Id, proposal.AskPrice);
```

**Also handle the error:**
```csharp
if (buyResult.Error?.Code == "InvalidProposal")
{
    _logger.LogWarning("Proposal expired, retrying...");
    // Retry logic
}
```

---

### 5. Deriv WebSocket Connection Drops

**Problem:** WebSocket connections to Deriv drop periodically (network issues, server restart, idle timeout).

**Solution:** Implement reconnection logic:
```csharp
private async Task EnsureConnectedAsync()
{
    if (_webSocket.State != WebSocketState.Open)
    {
        _logger.LogWarning("WebSocket disconnected, reconnecting...");
        await ConnectAsync();
        await AuthorizeAsync();
    }
}

// Call before every operation
public async Task<TradeResult> PlaceBinaryOptionAsync(...)
{
    await EnsureConnectedAsync();
    // ... rest of logic
}
```

---

### 6. Emoji Handling in Parsers

**Problem:** TRADEMASTER signals include emojis that break regex patterns:
```
😄TP 4348
😄SL 4366
```

**Solution:** Strip emojis before parsing OR use Unicode-aware patterns:
```csharp
// Option 1: Strip all emojis
private string StripEmojis(string input)
{
    return Regex.Replace(input, @"[\u0000-\u001F\u007F-\u009F\u00A0-\u00FF\u2000-\u3300\uD83C-\uDBFF\uDC00-\uDFFF\uFE00-\uFE0F\u200D]+", "");
}

// Option 2: Make pattern emoji-tolerant
var tpPattern = @"[\s\S]*?TP\s+(\d+\.?\d*)";
```

---

### 7. Case Sensitivity in Parsers

**Problem:** Different providers use different cases:
```
JAMSONTRADER:  "XAUUSD BUY 4352"
GOLD SIGNAL:   "Xauusd Sell 4374"
FOREXSIGNALS:  "VOLATILITY 50 1S INDEX"
```

**Solution:** Always normalize to uppercase before parsing:
```csharp
public async Task<ParsedSignal?> ParseAsync(string message, ...)
{
    var normalized = message.ToUpperInvariant();
    // ... rest of parsing
}
```

---

### 8. Duration Units for Deriv API

**Problem:** Deriv API accepts different duration units:
- `t` = ticks
- `s` = seconds
- `m` = minutes
- `h` = hours
- `d` = days

**Your ExpiryTime is in MINUTES.** Convert appropriately:
```csharp
var durationUnit = "m";  // Always use minutes
var duration = providerConfig.ExpiryTime;

// But if > 60, might want hours:
if (duration >= 60 && duration % 60 == 0)
{
    durationUnit = "h";
    duration = duration / 60;
}
```

---

### 9. Forex Markets Are Closed on Weekends

**Problem:** Forex pairs (XAUUSD, EURUSD, etc.) can't be traded on weekends.

**Deriv Response:**
```json
{
  "error": {
    "code": "MarketIsClosed",
    "message": "This market is presently closed."
  }
}
```

**Solution:**
```csharp
if (error.Code == "MarketIsClosed")
{
    _logger.LogWarning("Market closed for {Asset}, skipping", asset);
    // Don't mark as processed - retry when market opens
    // OR mark processed and log the skip
}
```

---

### 10. Duplicate Signal Prevention

**Problem:** Same signal might be received multiple times (Telegram edit, republish, etc.).

**Solution:** Use unique constraint + catch violation:
```csharp
try
{
    await _connection.ExecuteAsync(insertSql, signal);
}
catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
{
    _logger.LogDebug("Duplicate signal ignored: {Asset} {Direction}", signal.Asset, signal.Direction);
    return existingSignalId;  // Return existing instead
}
```

---

### 11. WTelegram Session Files

**Problem:** WTelegram stores session files. If corrupted or phone number changes, auth fails.

**Location:** `wt_session.dat` (or similar, in working directory)

**Solution:**
- On auth failure, delete session file and re-authenticate
- Store session files in dedicated folder
- Log the session file location

```csharp
static string Config(string what)
{
    switch (what)
    {
        case "session_pathname": return "sessions/bods_session.dat";
        // ... other config
    }
}
```

---

### 12. Deriv Asset Symbol Mapping Is Critical

**Problem:** Provider says "VOLATILITY 50 1S INDEX" but Deriv API needs "1HZ50V".

**Common Mappings:**
| Provider Says | Deriv Symbol |
|--------------|--------------|
| VOLATILITY 50 INDEX | R_50 |
| VOLATILITY 50 1S INDEX | 1HZ50V |
| STEP INDEX | stpRNG |
| XAUUSD | frxXAUUSD |
| EUR/USD | frxEURUSD |

**GOTCHA:** The mapping must handle variations:
- "Vol 50" vs "VOLATILITY 50"
- "V75(1s)" vs "VOLATILITY 75 1S"
- With/without "INDEX" suffix

---

### 13. Trade Result Monitoring

**Problem:** After placing a trade, you need to know if it won/lost for notifications.

**Option 1: Poll after expiry**
```csharp
var expectedExpiry = DateTime.UtcNow.AddMinutes(expiryMinutes);
await Task.Delay(expectedExpiry - DateTime.UtcNow + TimeSpan.FromSeconds(10));
var result = await GetContractResultAsync(contractId);
```

**Option 2: Subscribe to contract updates**
```json
{
  "proposal_open_contract": 1,
  "contract_id": "CONTRACT_ID",
  "subscribe": 1
}
```
Then listen for `is_sold` or `status` changes.

---

### 14. Rate Limiting on Deriv API

**Problem:** Deriv has rate limits. Too many requests = temporary block.

**Solution:**
```csharp
private readonly SemaphoreSlim _rateLimiter = new(1, 1);
private DateTime _lastRequest = DateTime.MinValue;
private const int MinDelayMs = 500;  // 500ms between requests

private async Task RateLimitAsync()
{
    await _rateLimiter.WaitAsync();
    try
    {
        var elapsed = DateTime.UtcNow - _lastRequest;
        if (elapsed.TotalMilliseconds < MinDelayMs)
        {
            await Task.Delay(MinDelayMs - (int)elapsed.TotalMilliseconds);
        }
        _lastRequest = DateTime.UtcNow;
    }
    finally
    {
        _rateLimiter.Release();
    }
}
```

---

### 15. TakeOriginal vs TakeOpposite

**Problem:** Some providers have reversed signals (they're always wrong, so do opposite).

**Logic:**
```csharp
var config = await GetProviderConfigAsync(signal.ProviderChannelId);

if (config.TakeOriginal)
{
    await ExecuteTradeAsync(signal.Direction);  // BUY stays BUY
}

if (config.TakeOpposite)
{
    var opposite = signal.Direction == "BUY" ? "SELL" : "BUY";
    await ExecuteTradeAsync(opposite);  // BUY becomes SELL
}

// Note: Both can be true! You'd place 2 trades then.
```

---

### 16. Price Decimal Precision

**Problem:** Different assets have different decimal places:
- XAUUSD: 4352.50 (2 decimals)
- EURUSD: 1.08542 (5 decimals)
- Volatility: 199656.00 (2 decimals)

**Solution:** Use `DECIMAL(18,5)` in database and let Deriv handle formatting:
```csharp
// Parse with decimal, don't format
if (decimal.TryParse(priceStr, out var price))
{
    signal.EntryPrice = price;
}
```

---

### 17. Multiple TP Levels - Which to Use?

**Problem:** Signals often have TP1, TP2, TP3. Binary options don't have take profits.

**For Rise/Fall:** TP levels are informational only. The trade closes at expiry, not at TP.

**Store them anyway for:**
- Analytics (how far did price go?)
- Future features
- Logging/debugging

---

### 18. Direction Mapping

**CRITICAL:** Be consistent with terminology:

| Signal Says | Store As | Send to Deriv |
|-------------|----------|---------------|
| BUY | BUY | CALL |
| SELL | SELL | PUT |
| CALL | BUY | CALL |
| PUT | SELL | PUT |
| UP | BUY | CALL |
| DOWN | SELL | PUT |

```csharp
private string MapToDerivContractType(string direction)
{
    return direction.ToUpperInvariant() switch
    {
        "BUY" or "CALL" or "UP" or "RISE" => "CALL",
        "SELL" or "PUT" or "DOWN" or "FALL" => "PUT",
        _ => throw new ArgumentException($"Unknown direction: {direction}")
    };
}
```

---

## 🔧 Testing Recommendations

### 1. Use a Test/Demo Deriv Account
Create a demo account on Deriv for testing. Never test with real money.

### 2. Create a Test Telegram Channel
Set up your own channel to send test signals and verify parsing works.

### 3. Mock Deriv Responses
For unit tests, mock the WebSocket responses:
```csharp
var mockClient = new Mock<IDerivClient>();
mockClient.Setup(x => x.PlaceBinaryOptionAsync(...))
    .ReturnsAsync(new TradeResult { ContractId = "TEST123" });
```

### 4. Test Each Parser Independently
```csharp
[Theory]
[InlineData("XAUUSD BUY 4352", "XAUUSD", "BUY", 4352)]
[InlineData("XAUUSD SELL 4350", "XAUUSD", "SELL", 4350)]
public async Task JamsonParser_ParsesCorrectly(string message, string asset, string dir, decimal entry)
{
    var parser = new JamsonTraderParser();
    var result = await parser.ParseAsync(message, "-1001641563130");

    Assert.NotNull(result);
    Assert.Equal(asset, result.Asset);
    Assert.Equal(dir, result.Direction);
    Assert.Equal(entry, result.EntryPrice);
}
```

---

## 📋 Checklist Before Going Live

- [ ] All 5 parsers implemented and tested
- [ ] Database tables created in BODS database
- [ ] Provider configs inserted
- [ ] Deriv API connection working (demo first)
- [ ] Telegram client connecting to all channels
- [ ] Boom/Crash filtering in place
- [ ] Error handling for market closed
- [ ] Duplicate signal prevention working
- [ ] Logging configured properly
- [ ] WebSocket reconnection logic tested
- [ ] Rate limiting implemented
- [ ] Trade result monitoring (optional but recommended)

---

## 🔗 Reference: DerivCTraderAutomation Files to Study

| Concept | File Location |
|---------|---------------|
| Telegram scraping | `src/DerivCTrader.SignalScraper/Services/TelegramSignalScraperService.cs` |
| Parser implementation | `src/DerivCTrader.Application/Parsers/SyntheticIndicesParser.cs` |
| Parser interface | `src/DerivCTrader.Application/Interfaces/ISignalParser.cs` |
| Binary execution | `src/DerivCTrader.TradeExecutor/Services/BinaryExecutionService.cs` |
| Deriv client | `src/DerivCTrader.Infrastructure/Deriv/DerivClient.cs` |
| Asset mapping | `src/DerivCTrader.Infrastructure/Deriv/DerivAssetMapper.cs` |
| Database operations | `src/DerivCTrader.Infrastructure/Persistence/SqlServerTradeRepository.cs` |
| Config loading | `src/DerivCTrader.SignalScraper/Program.cs` |

---

## ⚠️ Security Notes

1. **Never commit API tokens** - Use environment variables or user secrets
2. **Telegram session files** - Add to .gitignore
3. **Connection strings** - Use user secrets or environment variables
4. **Demo vs Live** - Have separate configs and make it obvious which is which

```csharp
if (config.Environment == "Live")
{
    _logger.LogWarning("⚠️ RUNNING IN LIVE MODE - REAL MONEY AT RISK ⚠️");
}
```


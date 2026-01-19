# DerivGold MT5 Implementation - Complete Package

## ?? What's Included

This package contains **complete implementation** for scraping and executing signals from 8 Gold/synthetic trading channels on MT5 + Deriv.

### ? Files Created

**Parsers** (4 files):
1. `src/DerivMT5.Application/Parsers/GoldSignalParser1.cs` - Channel `-1001357835235`
2. `src/DerivMT5.Application/Parsers/MegaSpikesParser.cs` - Channel `-1001060006944` (Boom/Crash)
3. `src/DerivMT5.Application/Parsers/VolatilitySignalsParser.cs` - Channel `-1001768939027`
4. `src/DerivMT5.Application/Parsers/GoldUnifiedParser.cs` - 5 channels (Gold, US30, VIX)

**Configuration**:
- `Database/InsertDerivGoldProviders.sql` - Provider config SQL script

**Documentation**:
- `DERIVGOLD_PARSERS_IMPLEMENTATION.md` - Complete implementation guide
- `ARCHITECTURE_MT5.md` - MT5 architecture (already created)

**Code Updates**:
- Updated `SignalType` enum with `MarketExecution` type

---

## ?? Supported Channels & Assets

| Channel ID | Assets | Parser | Binary? |
|------------|--------|--------|---------|
| `-1001357835235` | XAUUSD | `GoldSignalParser1` | ? Yes |
| `-1001060006944` | Boom/Crash | `MegaSpikesParser` | ? **NO** |
| `-1001768939027` | Volatility 25/50/75/100 | `VolatilitySignalsParser` | ? Yes |
| `-1002782055957` | XAUUSD | `GoldUnifiedParser` | ? Yes |
| `-1001685029638` | XAUUSD | `GoldUnifiedParser` | ? Yes |
| `-1001631556618` | XAUUSD, US30 | `GoldUnifiedParser` | ? Yes |
| `-1002242743399` | XAUUSD | `GoldUnifiedParser` | ? Yes |
| `-1003046812685` | VIX15, VIX25 | `GoldUnifiedParser` | ? Yes |

**Total**: 8 channels, ~10 different asset types

---

## ?? Quick Start

### Step 1: Copy Parser Files

Copy the 4 parser files to your project:
```
DerivCTraderAutomation/
??? src/
?   ??? DerivMT5.Application/
?   ?   ??? Parsers/
?   ?   ?   ??? GoldSignalParser1.cs ? NEW
?   ?   ?   ??? MegaSpikesParser.cs ? NEW
?   ?   ?   ??? VolatilitySignalsParser.cs ? NEW
?   ?   ?   ??? GoldUnifiedParser.cs ? NEW
```

### Step 2: Update Namespace

Change namespace in parser files from:
```csharp
namespace DerivMT5.Application.Parsers;
```

To match your project:
```csharp
namespace DerivCTrader.Application.Parsers;
```

### Step 3: Register Parsers

In `SignalScraper/Program.cs`:
```csharp
// Add after existing parser registrations
services.AddSingleton<ISignalParser, GoldSignalParser1>();
services.AddSingleton<ISignalParser, MegaSpikesParser>();
services.AddSingleton<ISignalParser, VolatilitySignalsParser>();
services.AddSingleton<ISignalParser, GoldUnifiedParser>();
```

### Step 4: Configure Database

Run SQL script:
```sql
-- File: Database/InsertDerivGoldProviders.sql
-- Inserts all 8 channel configurations
```

### Step 5: Test Parsing

Send test signals to channels and verify parsing:
```powershell
# Check logs for successful parsing
tail -f logs/signal-scraper-*.log | grep "DerivGold"
```

---

## ?? Implementation Checklist

### ? Phase 1: Core Parsers (COMPLETE)
- [x] Create 4 parser files
- [x] Handle midpoint calculation (e.g., `4335/4338`)
- [x] Handle first-value TP extraction (e.g., `4300/4285`)
- [x] Handle various TP/SL formats (`:`, `.`, `_`, space)
- [x] Handle market vs pending orders
- [x] Add `SignalType.MarketExecution`

### ?? Phase 2: Integration (TODO - Next Steps)
- [ ] Copy parser files to your project
- [ ] Update namespaces to match your project
- [ ] Register parsers in `Program.cs`
- [ ] Run database configuration script
- [ ] Test with live signals

### ?? Phase 3: MT5 Execution (TODO - See ARCHITECTURE_MT5.md)
- [ ] Choose MT5 integration method (MetaApi vs ZeroMQ vs REST Bridge)
- [ ] Implement `MT5RestClient` or `MT5ZeroMQClient`
- [ ] Implement pending order logic
- [ ] Implement market order logic
- [ ] Add Boom/Crash binary exclusion

### ?? Phase 4: Deriv Binary (TODO)
- [ ] Add Boom/Crash detection
- [ ] Skip binary execution for Boom/Crash
- [ ] Execute binary for Gold/Volatility/VIX
- [ ] Configure expiry times (15min/30min)

---

## ?? Critical Implementation Notes

### 1. Boom/Crash - NO DERIV BINARY

**Boom and Crash indices do NOT support Deriv binary options**.

**Affected Channel**: `-1001060006944` (Mega Spikes Max)

**Implementation**:
```csharp
// In your execution service
if (signal.Asset.Contains("Boom") || signal.Asset.Contains("Crash"))
{
    _logger.LogInformation("Boom/Crash detected - skipping Deriv binary");
    
    // Execute on MT5 only
    await _mt5Client.ExecuteMarketOrderAsync(signal);
    
    // DO NOT execute Deriv binary
    return;
}

// For all other assets (Gold, Volatility, VIX, US30)
await _mt5Client.ExecuteOrderAsync(signal);
await _derivClient.ExecuteBinaryAsync(signal); // ? Execute binary
```

### 2. Entry Price Midpoint

When signal has two prices (e.g., `SELL 4335/4338`, `SELL 4335 OR 4338`, `SELL 4335 MORE SELL 4338`):

**Always use midpoint**:
```csharp
var entryPrice = (price1 + price2) / 2m;
```

### 3. TP First Value

When TP has two values (e.g., `TP3. 4300/4285`):

**Always use first value**:
```csharp
tp3 = 4300m; // NOT 4285
```

### 4. Smart Order Logic

For signals like `SELL NOW 748900` (entry price provided):

**BUY Signal**:
```
If CurrentPrice <= EntryPrice ? Market Order (execute immediately)
If CurrentPrice > EntryPrice ? Pending Buy Limit Order
```

**SELL Signal**:
```
If CurrentPrice >= EntryPrice ? Market Order (execute immediately)
If CurrentPrice < EntryPrice ? Pending Sell Limit Order
```

### 5. SL PREMIUM

When signal shows `SL: PREMIUM` ? This means **no stop loss**:
```csharp
signal.StopLoss = null; // NO SL
```

---

## ?? Testing Guide

### Test Cases

**Test 1: Midpoint Calculation**
```
Input: "XAUUSD SELL 4335/4338\nTP1. 4325\nSL. 4341"
Expected: EntryPrice = 4336.5 (midpoint)
```

**Test 2: First TP Value**
```
Input: "GOLD BUY 4320\nTP3. 4300/4285\nSL. 4315"
Expected: TakeProfit3 = 4300 (not 4285)
```

**Test 3: Market Execution**
```
Input: "VOLATILITY 50 1S INDEX\nBUY NOW"
Expected: EntryPrice = null, SignalType = MarketExecution
```

**Test 4: Boom Asset (No Binary)**
```
Input: "??Boom 600 Index\n??Buy\n??TP 1: 6506"
Expected: Asset contains "Boom", must skip Deriv binary
```

**Test 5: Multiple TPs**
```
Input: "GOLD SELL 4331\nTP1 4328\nTP2 4325\nTP3 4322\nTPs 4315\nSL_4344"
Expected: tp1=4328, tp2=4325, tp3=4322, tp4=4315, sl=4344
```

---

## ?? Strategy Name

**ALL signals use the same strategy name**: `"DerivGold"`

This is hardcoded in each parser:
```csharp
signal.ProviderName = "DerivGold";
```

This ensures all signals from these 8 channels are grouped together for analysis.

---

## ?? Related Documentation

- **`DERIVGOLD_PARSERS_IMPLEMENTATION.md`** - Detailed implementation guide with all signal formats
- **`ARCHITECTURE_MT5.md`** - Complete MT5 architecture and integration options
- **`Database/InsertDerivGoldProviders.sql`** - Database configuration script

---

## ?? Support & Troubleshooting

### Parser Not Matching

**Symptoms**: Signal received but not parsed

**Causes**:
1. Channel ID mismatch
2. Signal format changed
3. Typo in pattern regex

**Debug Steps**:
1. Check `CanParse()` returns true
2. Enable verbose logging
3. Test regex pattern with sample message
4. Check for emoji/special characters

### Entry Price Incorrect

**Symptoms**: Wrong entry price calculated

**Causes**:
1. Not using midpoint for dual prices
2. Decimal parse error
3. Wrong price group in regex

**Debug Steps**:
1. Log extracted price values before calculation
2. Verify regex groups match expected positions
3. Check for culture-specific decimal separators

### Boom/Crash Executing Binary

**Symptoms**: Binary execution attempted for Boom/Crash

**Fix**: Add asset check before Deriv execution:
```csharp
if (signal.Asset.Contains("Boom", StringComparison.OrdinalIgnoreCase) ||
    signal.Asset.Contains("Crash", StringComparison.OrdinalIgnoreCase))
{
    // Skip binary execution
    return;
}
```

---

## ?? Next Steps

1. ? **Review Parser Code** - Understand each parser's logic
2. ? **Copy Files** - Add parsers to your project
3. ? **Update Namespaces** - Match your project structure
4. ? **Register Parsers** - Add to DI container
5. ? **Configure DB** - Run SQL script
6. ? **Test Parsing** - Send test signals and verify
7. ? **Implement MT5** - Choose integration method (see ARCHITECTURE_MT5.md)
8. ? **Add Execution Logic** - Pending vs market orders
9. ? **Deploy & Monitor** - Start with demo account

---

**Date**: December 16, 2025  
**Version**: 1.0  
**Status**: Ready for integration ?

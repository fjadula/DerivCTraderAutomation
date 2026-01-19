# DerivGold MT5 Signal Parsers - Complete Implementation Guide

## ?? Overview

This document describes the implementation of **9 new signal parsers** for the DerivGold trading system, covering Gold (XAUUSD), US30, Boom/Crash indices, Volatility indices, and VIX signals.

**Strategy Name**: All signals use `"DerivGold"` as the `ProviderName`.

---

## ?? Channels & Parsers

| Channel ID | Channel Name | Parser | Assets | Notes |
|------------|--------------|--------|--------|-------|
| `-1001357835235` | Gold Signal 1 | `GoldSignalParser1` | XAUUSD | Midpoint pricing |
| `-1001060006944` | Mega Spikes Max | `MegaSpikesParser` | Boom/Crash | **No Deriv binary** |
| `-1001768939027` | Volatility Signals | `VolatilitySignalsParser` | Volatility indices | Market & pending |
| `-1002782055957` | Gold Signal 2 | `GoldUnifiedParser` | XAUUSD | Simple format |
| `-1001685029638` | Gold Signal 3 | `GoldUnifiedParser` | XAUUSD | "MORE SELL" format |
| `-1001631556618` | Gold/US30 Signals | `GoldUnifiedParser` | XAUUSD, US30 | Multi-asset |
| `-1002242743399` | Gold Signal 4 | `GoldUnifiedParser` | XAUUSD | Standard format |
| `-1003046812685` | VIX Signals | `GoldUnifiedParser` | VIX15, VIX25 | Lot size included |

---

## ?? Signal Format Examples & Parsing Rules

### 1. GoldSignalParser1 (`-1001357835235`)

**Format:**
```
XAUUSD SELL 4335/4338
TP1. 4325
TP2. 4315
TP3. 4300/4285
SL. 4341
```

**Parsing Rules:**
- ? If two entry prices (4335/4338) ? Use **midpoint**: `(4335 + 4338) / 2 = 4336.5`
- ? If TP has two values (4300/4285) ? Use **first value**: `4300`
- ? TPs can be numbered (TP1, TP2, TP3) or unnumbered
- ? SL can have `.`, `:`, or space separator

**Signal Type:** `SignalType.Text` (pending order at midpoint)

---

### 2. MegaSpikesParser (`-1001060006944`)

**Format:**
```
??MEGA SPIKES MAX
??Boom 600 Index
??M5
??Buy
??TP 1: 6506
??TP 2: 6527
?? SL: 6448
```

**Parsing Rules:**
- ? Extract asset from `Boom XXX Index` or `Crash XXX Index`
- ? Direction from emoji or text (`??Buy` or `Buy`)
- ? **IMPORTANT**: `EntryPrice = null` (instant market execution)
- ?? **NO DERIV BINARY** - Boom/Crash don't support binary options (Rise/Fall only)

**Signal Type:** `SignalType.MarketExecution`

---

### 3. VolatilitySignalsParser (`-1001768939027`)

**Format 1 (Market Execution):**
```
VOLATILITY 50 1S INDEX
BUY NOW
APPLY PROPER RISK MANAGEMENT
```

**Format 2 (Pending Order):**
```
VOLATILITY 25 (1s) SELL NOW 748900
TP 748000
TP 746500
TP 744500
```

**Parsing Rules:**
- ? Extract number from `VOLATILITY XX`
- ? If price after "NOW" (e.g., `SELL NOW 748900`) ? Pending order
- ? If no price ? Market execution
- ? **Smart Order Logic**:
  - **BUY with price**: If current price <= entry price ? Market order, else ? Pending order
  - **SELL with price**: If current price >= entry price ? Market order, else ? Pending order
- ? TPs are unnumbered (just `TP 748000`)

**Signal Type:** 
- `SignalType.MarketExecution` (no price)
- `SignalType.Text` (with price)

---

### 4. GoldUnifiedParser (`-1002782055957`)

**Format:**
```
BUY GOLD@ 4320
TP1: 4323
TP2: 4326+++
SL: PREMIUM ??
```

**Parsing Rules:**
- ? `@` symbol is optional
- ? `+++` after TP is ignored (just trailing info)
- ? `SL: PREMIUM` means **no SL** (`StopLoss = null`)
- ? Can have numbered or unnumbered TPs

---

### 5. GoldUnifiedParser (`-1001685029638`)

**Format:**
```
GOLD SELL 4331 MORE SELL 4335
TP1 4328
TP2 4325
TP3 4322
TP4 4319
TPs 4315
TPS 4312
TP 4309
SL_4344
```

**Parsing Rules:**
- ? Two prices with "MORE SELL" ? Use **midpoint**: `(4331 + 4335) / 2 = 4333`
- ? `SL_4344` (underscore separator) ? Extract `4344`
- ? Multiple TP formats: `TP1`, `TPs`, `TP` (all valid)

---

### 6. GoldUnifiedParser (`-1001631556618`) - Multi-Asset

**Format 1 (Gold):**
```
XAUUSD SELL 4190 OR 4194
SL 4202
TP 4186
TP 4183
TP 4180
TP 4175
TP 4170
TP 4165
```

**Format 2 (US30):**
```
US30 BUY 48150
SL 48020
TP 48180
TP 48250
TP 48350
TP 48650
```

**Parsing Rules:**
- ? "OR" between prices ? Use **midpoint**: `(4190 + 4194) / 2 = 4192`
- ? Can parse both `XAUUSD` and `US30` from same channel
- ? No TP numbers ? Assign sequentially (tp1, tp2, tp3, tp4)

---

### 7. GoldUnifiedParser (`-1002242743399`)

**Format:**
```
GOLD SELL 4320

TP1:4216
TP2:4312
TP3:4308
TP4:4304

STOPLOSS:4334
FOLLOW MONEY MANAGEMENT
```

**Parsing Rules:**
- ? `STOPLOSS` (full word) ? Extract SL value
- ? No spaces between `TP1:` and value ? Handles both formats

---

### 8. GoldUnifiedParser (`-1003046812685`)

**Format:**
```
BUY VIX15(1S)
@ 12970.220
Sl 12670.150
Tp 13270.250
Tp2 13570.500
Tp3 13870.750

Lotsize 1.5(2.5)
```

**Parsing Rules:**
- ? Extract asset: `VIX15`, `VIX25`, etc.
- ? Parse lot size: `1.5` (store in `ParsedSignal.LotSize`)
- ? High precision prices (3 decimal places)

---

## ?? Execution Logic Flow

### Pending Orders (SignalType.Text)

```
Signal received with EntryPrice
    ?
Create pending order on MT5
    ?
Monitor price cross
    ?
Price crosses entry in correct direction
    ?
Order fills ? Position opened
    ?
Execute Deriv binary (if applicable)
```

### Market Execution (SignalType.MarketExecution)

```
Signal received without EntryPrice
    ?
Execute market order immediately on MT5
    ?
Position opened
    ?
Execute Deriv binary (if applicable)
```

### Smart Pending/Market Logic

For signals with entry price (e.g., `SELL NOW 748900`):

**BUY Signal:**
```
Current Price <= Entry Price ? Execute market order immediately
Current Price > Entry Price ? Place pending buy limit order
```

**SELL Signal:**
```
Current Price >= Entry Price ? Execute market order immediately
Current Price < Entry Price ? Place pending sell limit order
```

---

## ?? Important Notes

### Boom/Crash - NO DERIV BINARY

**Boom/Crash indices do NOT support binary options** on Deriv:
- ? **No Rise/Fall contracts** available
- ? **No High/Low contracts** available
- ? Only MT5 execution (no binary mirroring)

**Affected Channel:** `-1001060006944` (Mega Spikes Max)

**Implementation:**
```csharp
// In execution service
if (signal.Asset.Contains("Boom") || signal.Asset.Contains("Crash"))
{
    // Execute on MT5 only, skip Deriv binary
    await _mt5Client.ExecuteMarketOrderAsync(signal);
    // NO Deriv binary execution
}
```

### Volatility Indices - HAS DERIV BINARY

**Volatility indices SUPPORT binary options**:
- ? Rise/Fall contracts available
- ? Standard 15min or 30min expiry

### Entry Price Handling

| Format | Entry Price | Order Type |
|--------|-------------|------------|
| `SELL 4335/4338` | Midpoint: `4336.5` | Pending |
| `SELL 4335 OR 4338` | Midpoint: `4336.5` | Pending |
| `SELL 4335 MORE SELL 4338` | Midpoint: `4336.5` | Pending |
| `SELL 4335` | Exact: `4335` | Pending |
| `SELL NOW` (no price) | `null` | Market |
| `SELL NOW 748900` | Conditional | Smart logic |

---

## ?? Database Configuration

### ProviderChannelConfig Table

```sql
INSERT INTO ProviderChannelConfig (ProviderChannelId, ProviderName, TakeOriginal, TakeOpposite, IsActive)
VALUES
    ('-1001357835235', 'DerivGold', 1, 0, 1),
    ('-1001060006944', 'DerivGold', 1, 0, 1),
    ('-1001768939027', 'DerivGold', 1, 0, 1),
    ('-1002782055957', 'DerivGold', 1, 0, 1),
    ('-1001685029638', 'DerivGold', 1, 0, 1),
    ('-1001631556618', 'DerivGold', 1, 0, 1),
    ('-1002242743399', 'DerivGold', 1, 0, 1),
    ('-1003046812685', 'DerivGold', 1, 0, 1);
```

### Expiry Configuration

Expiry times are stored in database (default: 15min for Volatility, 30min for Gold/US30).

---

## ??? Implementation Checklist

### ? Phase 1: Parsers (COMPLETE)
- [x] `GoldSignalParser1.cs` - Channel `-1001357835235`
- [x] `MegaSpikesParser.cs` - Channel `-1001060006944`
- [x] `VolatilitySignalsParser.cs` - Channel `-1001768939027`
- [x] `GoldUnifiedParser.cs` - Channels `-1002782055957`, `-1001685029638`, `-1001631556618`, `-1002242743399`, `-1003046812685`
- [x] Updated `SignalType` enum with `MarketExecution`

### ?? Phase 2: Integration (TODO)
- [ ] Register parsers in `Program.cs`
- [ ] Update `TelegramSignalScraperService` to handle new channels
- [ ] Configure MT5 symbol mappings
- [ ] Add Boom/Crash exclusion logic for Deriv binary

### ?? Phase 3: MT5 Execution (TODO)
- [ ] Implement MT5 pending order logic
- [ ] Implement MT5 market order logic
- [ ] Implement smart pending/market decision logic
- [ ] Add Boom/Crash detection and skip binary execution

### ?? Phase 4: Testing (TODO)
- [ ] Test each parser with sample messages
- [ ] Verify midpoint calculation
- [ ] Verify TP/SL extraction
- [ ] Test Boom/Crash exclusion
- [ ] Test Volatility binary execution

---

## ?? Testing Examples

### Test Cases

```csharp
// Test 1: Midpoint calculation
var message1 = "XAUUSD SELL 4335/4338\nTP1. 4325\nSL. 4341";
var signal1 = await parser1.ParseAsync(message1, "-1001357835235");
Assert.Equal(4336.5m, signal1.EntryPrice); // (4335 + 4338) / 2

// Test 2: TP with two values
var message2 = "GOLD BUY 4320\nTP3. 4300/4285\nSL. 4315";
var signal2 = await parser1.ParseAsync(message2, "-1001357835235");
Assert.Equal(4300m, signal2.TakeProfit3); // Use first value

// Test 3: Market execution
var message3 = "VOLATILITY 50 1S INDEX\nBUY NOW\nAPPLY PROPER RISK MANAGEMENT";
var signal3 = await volatilityParser.ParseAsync(message3, "-1001768939027");
Assert.Null(signal3.EntryPrice); // Market execution
Assert.Equal(SignalType.MarketExecution, signal3.SignalType);

// Test 4: Boom (no binary)
var message4 = "??MEGA SPIKES MAX\n??Boom 600 Index\n??Buy\n??TP 1: 6506";
var signal4 = await megaSpikesParser.ParseAsync(message4, "-1001060006944");
Assert.Contains("Boom", signal4.Asset);
// In execution: Skip Deriv binary for Boom/Crash
```

---

## ?? Support & Troubleshooting

### Common Issues

**Issue**: Entry price not extracted correctly
- **Solution**: Check if pattern matches the exact format (spacing, separators)
- **Debug**: Enable verbose logging in parser

**Issue**: TP values not extracted
- **Solution**: Verify TP format matches regex pattern (`:`, `.`, or space)
- **Debug**: Check `ExtractTakeProfits()` method logs

**Issue**: Boom/Crash executing Deriv binary
- **Solution**: Add asset name check before Deriv execution
- **Code**: `if (!signal.Asset.Contains("Boom") && !signal.Asset.Contains("Crash"))`

---

## ?? Next Steps

1. **Register Parsers** in `SignalScraper/Program.cs`:
```csharp
services.AddSingleton<ISignalParser, GoldSignalParser1>();
services.AddSingleton<ISignalParser, MegaSpikesParser>();
services.AddSingleton<ISignalParser, VolatilitySignalsParser>();
services.AddSingleton<ISignalParser, GoldUnifiedParser>();
```

2. **Configure Database**: Run SQL insert for `ProviderChannelConfig`

3. **Implement MT5 Client**: Create MT5 REST client or ZeroMQ bridge

4. **Add Execution Logic**: Handle pending vs market orders

5. **Test with Live Signals**: Deploy and monitor on demo account first

---

**Date**: December 16, 2025  
**Version**: 1.0  
**Status**: Parsers complete, ready for integration ?

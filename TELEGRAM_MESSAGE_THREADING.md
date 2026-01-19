# Telegram Message Reply Threading Implementation

## ?? Summary

Implemented Telegram message reply threading to enable conversation-style notifications for the trade lifecycle:
- **Order filled** ? Original notification message (starts the thread)
- **SL/TP modified (filled orders)** ? Reply to fill notification
- **Position closed** ? Reply to fill notification

**Note**: Signal received and order created do NOT send notifications. SL/TP modifications on **pending orders** (not yet filled) do NOT send notifications.

This creates clean message threads in Telegram:
```
?? GBPUSD Sell @ 1.2750, TP: 1.26500, SL: 1.28000
  ?? ?? SL/TP Modified: GBPUSD Sell - SL: 1.27300, TP: 1.26000
  ?? ?? CLOSED @ 1.2700 - ? Profit (TP Hit) - R:R=1:2
```

---

## ?? Problem Solved

**Before**: All trade notifications were standalone messages, making it hard to track which close/modify belonged to which fill.

**After**: Fill, modify, and close messages are threaded together:
```
?? GBPUSD Sell @ 1.2750, TP: 1.26500, SL: 1.28000
  ?? ?? SL/TP Modified: GBPUSD Sell - SL: 1.27300, TP: 1.26000
  ?? ?? CLOSED @ 1.2700 - ? Profit (TP Hit) - R:R=1:2
```

---

## ? Features Implemented

### 1. **Telegram Message ID Tracking**

#### Added Fields:
- **ParsedSignal.TelegramMessageId**: Original message ID from Telegram channel (for reference only - no reply needed since no notification sent)
- **ParsedSignal.NotificationMessageId**: Reserved for future use (optional order creation notifications)
- **ForexTrade.TelegramMessageId**: Message ID of fill notification (used for threading modify/close replies)

### 2. **Enhanced Telegram Notifier**

#### New Method:
```csharp
Task<int?> SendTradeMessageAsync(string message, int replyToMessageId, CancellationToken cancellationToken = default);
```

- Sends message as reply using `reply_to_message_id` parameter
- Returns the `message_id` of sent message for future threading
- Graceful fallback if reply fails (sends as standalone message)

### 3. **Lifecycle Event Flow**

#### ? Signal Received (SignalScraper):
- Captures `message.id` from incoming Telegram signals
- Stores in `ParsedSignal.TelegramMessageId` (for audit trail only)
- **NO notification sent** - this is just the raw signal from provider

#### ? Order Created:
- **NO notification sent** (by design - to avoid spam)
- Optional: Can be enabled if needed (see "Future Enhancements" section)

#### ? Order Fill:
- `NotifyFillAsync` sends the **first notification** (starts the thread)
- Format: `?? GBPUSD Sell @ 1.2750, TP: 1.26500, SL: 1.28000`
- Stores returned `message_id` as `ForexTrade.TelegramMessageId`
- This message becomes the parent for modify/close notifications

#### ? SL/TP Modification (Filled Orders Only):
- **Pending orders** (not yet filled): ? NO notification sent (silent database update)
- **Active positions** (filled): ? Notification sent as reply to fill notification
- Format: `?? SL/TP Modified: GBPUSD Sell - SL: 1.27300, TP: 1.26000`
- Database updated with new SL/TP values
- Logged in `ForexTrade.Notes` for audit trail

#### ? Position Close:
- Reply to fill notification (`ForexTrade.TelegramMessageId`)
- Shows final P&L, R:R, and close reason

---

## ?? Database Schema Changes

### SQL Migration Required

Run this script on your database:

```sql
-- Add to ParsedSignalsQueue
ALTER TABLE ParsedSignalsQueue
ADD TelegramMessageId INT NULL,
    NotificationMessageId INT NULL;

-- Add to ForexTrades
ALTER TABLE ForexTrades
ADD TelegramMessageId INT NULL;
```

See: `Database/AddTelegramMessageIdColumns.sql`

---

## ?? Message Flow Example

### Example: Forex Trade Lifecycle

**Step 1: Signal Received**
```
SignalScraper receives Telegram message:
  Channel: -1001138473049 (VIPFX)
  MessageId: 12345
  Text: "We are selling GBPUSD now at 1.27500..."
  
Saved to DB:
  SignalId: 789
  TelegramMessageId: 12345
  
? NO notification sent to your chat
```

**Step 2: Order Created**
```
TradeExecutor creates pending order:
  OrderId: 98765
  
? NO notification sent (by design)
```

**Step 3: Order Filled** (FIRST NOTIFICATION)
```
cTrader executes order:
  PositionId: 18765432
  ExecutedPrice: 1.27485
  
Send notification:
  Message: "?? GBPUSD Sell @ 1.27500, TP: 1.26500, SL: 1.28000"
  ReplyTo: (none - this is the FIRST message)
  
Returned MessageId: 67890
Saved as: ForexTrade.TelegramMessageId = 67890
```

**Step 4: SL/TP Modified (Active Position)**
```
User manually adjusts SL/TP in cTrader:
  NewSL: 1.27300
  NewTP: 1.26000
  
Database updated:
  ForexTrade.SL = 1.27300
  ForexTrade.TP = 1.26000
  ForexTrade.Notes += "SLTPModified@14:45:32;NewSL=1.273;NewTP=1.260"
  
Send notification:
  Message: "?? SL/TP Modified: GBPUSD Sell\nSL: 1.27300, TP: 1.26000"
  ReplyTo: 67890 (fill notification)
  
? Notification sent as reply to fill message
```

**Step 5: Position Closed**
```
Position hits TP:
  ExitPrice: 1.26000
  PnL: +150.00
  
Send notification:
  Message: "?? CLOSED GBPUSD Sell @ 1.260\n? PnL=Profit Reason=TP Hit\n?? R:R=1:3"
  ReplyTo: 67890 (fill notification from Step 3)
```

### Telegram View:
```
[67890] ?? GBPUSD Sell @ 1.27500, TP: 1.26500, SL: 1.28000
  ?? ?? SL/TP Modified: GBPUSD Sell - SL: 1.27300, TP: 1.26000
  ?? ?? CLOSED GBPUSD Sell @ 1.260 - ? Profit (TP Hit) - R:R=1:3
```

**Clean threading** showing the complete trade lifecycle!

---

## ?? Usage in Code

### In SignalScraper Service:

```csharp
// Capture message ID when receiving signal (for audit only)
if (update is UpdateNewMessage { message: Message message })
{
    var parsedSignal = await parser.ParseAsync(message.message, channelId);
    if (parsedSignal != null)
    {
        parsedSignal.TelegramMessageId = message.id; // ? Captured for audit
        await _repository.SaveParsedSignalAsync(parsedSignal);
        // ? NO notification sent
    }
}
```

### In CTraderPendingOrderService:

```csharp
// Send fill notification (first message - starts thread)
private async Task NotifyFillAsync(ParsedSignal signal, ...)
{
    var msg = $"?? {signal.Asset} {direction} @ {entry}, TP: {tp}, SL: {sl}";
    
    // Send as standalone message (no reply - this is the first notification)
    var messageId = await _telegram.SendTradeMessageAsync(msg);
    
    // Store message_id in ForexTrade for threading modify/close notifications
}

// SL/TP modification - notification sent for filled orders only
private async Task HandleSlTpModificationAsync(...)
{
    if (isPendingOrder)
    {
        // Update database only - NO notification
        await _repository.UpdateParsedSignalSlTpAsync(...);
        return;
    }
    
    // For active positions (filled orders)
    await _repository.UpdateForexTradeAsync(trade);
    
    var msg = $"?? SL/TP Modified: {trade.Symbol} {trade.Direction}\nSL: {newSL}, TP: {newTP}";
    
    if (trade.TelegramMessageId.HasValue)
    {
        await _telegram.SendTradeMessageAsync(msg, trade.TelegramMessageId.Value); // Reply
    }
}

// Send close notification as reply to fill
private async Task HandlePositionClosedAsync(...)
{
    var msg = FormatCloseMessage(...);
    
    if (trade.TelegramMessageId.HasValue)
    {
        await _telegram.SendTradeMessageAsync(msg, trade.TelegramMessageId.Value); // Reply
    }
}
```

---

## ?? Important Notes

### 1. **Smart Notification Strategy**

The implementation balances information needs with spam reduction:
- ? NO notification when signal is received (raw provider signal)
- ? NO notification when order is created (would be too noisy)
- ? Notification when order fills (important - shows execution)
- ? NO notification when **pending order** SL/TP modified (order not filled yet)
- ? Notification when **active position** SL/TP modified (important - shows risk management)
- ? Notification when position closes (important - shows final result)

### 2. **Database Tracking**

All events are tracked in the database:
- `ParsedSignalsQueue`: Raw signal data + original Telegram message_id
- `ForexTrades`: Full trade lifecycle including SL/TP modifications in Notes
- `ForexTrade.TelegramMessageId`: Fill notification message_id for threading

### 3. **SL/TP Modifications**

**Pending Orders** (not yet filled):
- ? NO notification sent
- Database updated silently
- Logged in `ParsedSignalQueue` SL/TP fields

**Active Positions** (filled):
- ? Notification sent as reply to fill
- Database updated with new values
- Logged in `ForexTrade.Notes`: `SLTPModified@12:29:48;NewSL=1.273;NewTP=1.260`

---

## ?? Testing

### Test Scenario: Full Lifecycle
1. Send test signal to TestChannel
2. Verify no notification sent (signal captured in DB only)
3. Wait for order fill
4. ? Check Telegram: fill notification appears (format: `?? GBPUSD Sell @ 1.27500, TP: 1.26500, SL: 1.28000`)
5. Manually modify SL/TP in cTrader
6. ? Check Telegram: modification notification appears as **reply to fill message**
7. Close position
8. ? Check Telegram: close notification appears as **reply to fill message**

---

## ?? Future Enhancements

### Optional: Order Creation Notifications

If you want to enable order creation notifications:

Add this to `CTraderPendingOrderService.ProcessSignalAsync` after order creation:

```csharp
if (pendingResult.Success && pendingResult.OrderId.HasValue)
{
    var orderMsg = $"?? ORDER CREATED\n{signal.Asset} {effectiveDirection} @ {signal.EntryPrice}";
    
    var notificationMsgId = await _telegram.SendTradeMessageAsync(orderMsg);
    
    // Store for threading fill notification
    if (notificationMsgId.HasValue && signal.SignalId > 0)
    {
        await _repository.UpdateParsedSignalNotificationMessageIdAsync(signal.SignalId, notificationMsgId.Value);
    }
}
```

Then update fill notification to reply to order notification instead.

---

## ?? Related Files

### Modified Files:
- ? `src/DerivCTrader.Domain/Entities/ParsedSignal.cs`
- ? `src/DerivCTrader.Domain/Entities/ForexTrade.cs`
- ? `src/DerivCTrader.Application/Interfaces/ITelegramNotifier.cs`
- ? `src/DerivCTrader.Infrastructure/Notifications/TelegramNotifier.cs`
- ? `src/DerivCTrader.Infrastructure/CTrader/CTraderPendingOrderService.cs`
- ? `src/DerivCTrader.SignalScraper/Services/TelegramSignalScraperService.cs`
- ? `src/DerivCTrader.Infrastructure/Persistence/SqlServerTradeRepository.cs`

### New Files:
- ? `Database/AddTelegramMessageIdColumns.sql` (migration script)

---

## ? Status

**Implementation Status**: Complete ?

**Remaining Work**:
1. Run SQL migration on your database
2. Test with live signals
3. Verify fill ? modify ? close message threading works

**Database Migration Required**: Yes - run `Database/AddTelegramMessageIdColumns.sql`

---

## ?? Benefits

1. **Organized Chat**: Threaded messages show complete trade lifecycle
2. **Risk Management Visibility**: See SL/TP modifications immediately
3. **Easy Tracking**: Reply threading shows which modify/close belongs to which fill
4. **Clean Format**: Concise message format (`?? GBPUSD Sell @ 1.27500, TP: 1.26500, SL: 1.28000`)
5. **Full Audit Trail**: All events tracked in database
6. **Smart Spam Control**: Only notifies for filled orders, not pending orders

---

**Date**: December 16, 2025  
**Status**: Ready for testing ?

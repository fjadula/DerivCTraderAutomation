using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using DerivCTrader.Infrastructure.CTrader.Interfaces;
using DerivCTrader.Infrastructure.CTrader.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// Background service that processes ChartSense image-based signals.
///
/// ChartSense is unique:
/// - Signals come from chart images (OCR extracts asset, direction, timeframe, pattern)
/// - Only ONE active setup per asset at any time
/// - Direction flip = cancel pending order OR close filled position
/// - No SL/TP - positions run without protective orders
///
/// Flow:
/// 1. Poll ParsedSignalsQueue for unprocessed ChartSense signals
/// 2. Check for existing active setup for the same asset
/// 3. If direction changed: invalidate old setup (cancel order / close position)
/// 4. Create new ChartSenseSetup with derived entry price
/// 5. Place pending order on cTrader
/// 6. Update setup with CTraderOrderId
/// 7. Mark signal as processed
/// </summary>
public class ChartSenseSetupService : BackgroundService
{
    private const string ChartSenseChannelId = "-1001200022443";

    private readonly ILogger<ChartSenseSetupService> _logger;
    private readonly ITradeRepository _repository;
    private readonly ICTraderPendingOrderService _pendingOrderService;
    private readonly ICTraderSymbolService _symbolService;
    private readonly ICTraderOrderManager _orderManager;
    private readonly ITelegramNotifier _notifier;
    private readonly IConfiguration _configuration;
    private readonly int _pollIntervalSeconds;

    private readonly ConcurrentDictionary<int, byte> _inFlightSignals = new();

    // Timeout rules by timeframe (in hours)
    private static readonly Dictionary<string, int> TimeoutHoursByTimeframe = new(StringComparer.OrdinalIgnoreCase)
    {
        { "M15", 5 },
        { "M30", 7 },
        { "H1", 10 },
        { "1H", 10 },
        { "H2", 14 },
        { "2H", 14 },
        { "H4", 16 },
        { "4H", 16 },
        { "D1", 48 },
        { "1D", 48 },
        { "DAILY", 48 }
    };

    public ChartSenseSetupService(
        ILogger<ChartSenseSetupService> logger,
        ITradeRepository repository,
        ICTraderPendingOrderService pendingOrderService,
        ICTraderSymbolService symbolService,
        ICTraderOrderManager orderManager,
        ITelegramNotifier notifier,
        IConfiguration configuration)
    {
        _logger = logger;
        _repository = repository;
        _pendingOrderService = pendingOrderService;
        _symbolService = symbolService;
        _orderManager = orderManager;
        _notifier = notifier;
        _configuration = configuration;
        _pollIntervalSeconds = configuration.GetValue("CTrader:ChartSensePollIntervalSeconds", 10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== CHARTSENSE SETUP SERVICE STARTED ===");
        Console.WriteLine("========================================");
        Console.WriteLine("  ChartSense Image Signal Processor");
        Console.WriteLine("========================================");
        Console.WriteLine($"📊 Poll Interval: {_pollIntervalSeconds}s");
        Console.WriteLine("🖼️ Monitoring for ChartSense image signals...");
        Console.WriteLine();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessChartSenseSignalsAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ChartSense processing loop");
                Console.WriteLine($"❌ ChartSense ERROR: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("=== CHARTSENSE SETUP SERVICE STOPPED ===");
    }

    private async Task ProcessChartSenseSignalsAsync(CancellationToken cancellationToken)
    {
        if (!_symbolService.IsInitialized)
        {
            _logger.LogDebug("cTrader symbols not initialized yet; skipping ChartSense processing");
            return;
        }

        // Get all unprocessed signals
        var signals = await _repository.GetUnprocessedSignalsAsync();

        // Filter for ChartSense signals only
        var chartSenseSignals = signals
            .Where(s => s.ProviderChannelId == ChartSenseChannelId)
            .ToList();

        if (chartSenseSignals.Count == 0)
            return;

        _logger.LogInformation("🖼️ Found {Count} unprocessed ChartSense signals", chartSenseSignals.Count);
        Console.WriteLine($"🖼️ Processing {chartSenseSignals.Count} ChartSense signal(s)...");

        foreach (var signal in chartSenseSignals)
        {
            try
            {
                await ProcessSingleChartSenseSignalAsync(signal, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process ChartSense signal {SignalId}", signal.SignalId);
                Console.WriteLine($"❌ Failed ChartSense signal #{signal.SignalId}: {ex.Message}");
            }
        }
    }

    private async Task ProcessSingleChartSenseSignalAsync(ParsedSignal signal, CancellationToken cancellationToken)
    {
        if (signal.SignalId > 0 && !_inFlightSignals.TryAdd(signal.SignalId, 0))
        {
            _logger.LogDebug("Skipping ChartSense SignalId={SignalId} - already in-flight", signal.SignalId);
            return;
        }

        try
        {
            _logger.LogInformation("🖼️ Processing ChartSense signal #{SignalId}: {Asset} {Direction} ({Pattern} on {Timeframe})",
                signal.SignalId, signal.Asset, signal.Direction, signal.Pattern, signal.Timeframe);

            Console.WriteLine($"🖼️ Signal #{signal.SignalId}: {signal.Asset} {signal.Direction} ({signal.Pattern} on {signal.Timeframe})");

            // Check for existing active setup for this asset
            var existingSetup = await _repository.GetActiveChartSenseSetupByAssetAsync(signal.Asset);

            if (existingSetup != null)
            {
                // Same direction? Skip - we're already tracking this
                if (existingSetup.Direction == signal.Direction)
                {
                    _logger.LogInformation("🔄 Same direction setup already exists for {Asset} - skipping", signal.Asset);
                    Console.WriteLine($"   🔄 Existing {existingSetup.Direction} setup for {signal.Asset} - skipping");

                    // Mark signal as processed since we're intentionally skipping
                    await _repository.MarkSignalAsProcessedAsync(signal.SignalId);
                    return;
                }

                // Direction flip detected! Cancel/close the existing setup
                _logger.LogWarning("⚡ DIRECTION FLIP: {Asset} {OldDirection} -> {NewDirection}",
                    signal.Asset, existingSetup.Direction, signal.Direction);
                Console.WriteLine($"   ⚡ DIRECTION FLIP: {existingSetup.Direction} -> {signal.Direction}");

                await InvalidateExistingSetupAsync(existingSetup, $"Direction flip to {signal.Direction}");
            }

            // Create new ChartSense setup
            var setup = await CreateChartSenseSetupAsync(signal);

            if (setup == null)
            {
                _logger.LogWarning("Failed to create ChartSense setup for signal #{SignalId}", signal.SignalId);
                return;
            }

            // Place pending order on cTrader
            var orderResult = await PlacePendingOrderAsync(signal, setup);

            if (orderResult.Success && orderResult.OrderId.HasValue)
            {
                // Update setup with cTrader order ID
                setup.CTraderOrderId = orderResult.OrderId.Value;
                setup.Status = ChartSenseStatus.PendingPlaced;
                setup.UpdatedAt = DateTime.UtcNow;
                await _repository.UpdateChartSenseSetupAsync(setup);

                _logger.LogInformation("✅ ChartSense pending order placed: {Asset} {Direction} OrderId={OrderId}",
                    signal.Asset, signal.Direction, orderResult.OrderId);
                Console.WriteLine($"   ✅ Pending order placed (OrderId: {orderResult.OrderId})");

                // Send notification
                await SendSetupNotificationAsync(setup, "created");
            }
            else
            {
                // Order failed - leave setup in Watching state for retry or manual intervention
                _logger.LogWarning("⚠️ Failed to place ChartSense order: {Error}", orderResult.ErrorMessage);
                Console.WriteLine($"   ⚠️ Order failed: {orderResult.ErrorMessage}");
            }

            // Mark signal as processed
            await _repository.MarkSignalAsProcessedAsync(signal.SignalId);
            _logger.LogInformation("✅ ChartSense signal #{SignalId} processed", signal.SignalId);
            Console.WriteLine();
        }
        finally
        {
            if (signal.SignalId > 0)
                _inFlightSignals.TryRemove(signal.SignalId, out _);
        }
    }

    private async Task InvalidateExistingSetupAsync(ChartSenseSetup setup, string reason)
    {
        try
        {
            if (setup.Status == ChartSenseStatus.PendingPlaced && setup.CTraderOrderId.HasValue)
            {
                // Cancel the pending order
                _logger.LogInformation("📛 Cancelling pending order {OrderId} for {Asset}",
                    setup.CTraderOrderId, setup.Asset);
                Console.WriteLine($"   📛 Cancelling pending order {setup.CTraderOrderId}...");

                var cancelled = await _orderManager.CancelOrderAsync(setup.CTraderOrderId.Value);
                if (cancelled)
                {
                    _logger.LogInformation("✅ Order {OrderId} cancelled successfully", setup.CTraderOrderId);
                    Console.WriteLine("   ✅ Order cancelled");
                }
                else
                {
                    _logger.LogWarning("⚠️ Failed to cancel order {OrderId}", setup.CTraderOrderId);
                    Console.WriteLine("   ⚠️ Failed to cancel order");
                }
            }
            else if (setup.Status == ChartSenseStatus.Filled && setup.CTraderPositionId.HasValue)
            {
                // Close the filled position
                _logger.LogInformation("📛 Closing position {PositionId} for {Asset}",
                    setup.CTraderPositionId, setup.Asset);
                Console.WriteLine($"   📛 Closing position {setup.CTraderPositionId}...");

                // Close full position (volume=0 means close all)
                var closed = await _orderManager.ClosePositionAsync(setup.CTraderPositionId.Value, 0);
                if (closed)
                {
                    _logger.LogInformation("✅ Position {PositionId} closed successfully", setup.CTraderPositionId);
                    Console.WriteLine("   ✅ Position closed");
                }
                else
                {
                    _logger.LogWarning("⚠️ Failed to close position {PositionId}", setup.CTraderPositionId);
                    Console.WriteLine("   ⚠️ Failed to close position");
                }
            }

            // Mark setup as invalidated
            setup.Status = ChartSenseStatus.Invalidated;
            setup.ClosedAt = DateTime.UtcNow;
            setup.UpdatedAt = DateTime.UtcNow;
            await _repository.UpdateChartSenseSetupAsync(setup);

            // Send notification
            await SendSetupNotificationAsync(setup, $"invalidated ({reason})");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invalidating setup {SetupId} for {Asset}", setup.SetupId, setup.Asset);
        }
    }

    private async Task<ChartSenseSetup?> CreateChartSenseSetupAsync(ParsedSignal signal)
    {
        try
        {
            // Calculate timeout based on timeframe
            var timeoutHours = GetTimeoutHours(signal.Timeframe);
            var timeoutAt = DateTime.UtcNow.AddHours(timeoutHours);

            // Determine pattern classification (Reaction vs Breakout)
            var classification = DeterminePatternClassification(signal.Pattern);

            // Calculate entry buffer based on asset type
            var entryBuffer = GetEntryBuffer(signal.Asset);
            decimal? entryZoneMin = null;
            decimal? entryZoneMax = null;

            if (signal.EntryPrice.HasValue)
            {
                entryZoneMin = signal.EntryPrice.Value - entryBuffer;
                entryZoneMax = signal.EntryPrice.Value + entryBuffer;
            }

            var setup = new ChartSenseSetup
            {
                Asset = signal.Asset,
                Direction = signal.Direction,
                Timeframe = signal.Timeframe ?? "H1",
                PatternType = signal.Pattern ?? "Unknown",
                PatternClassification = classification,
                EntryPrice = signal.EntryPrice,
                EntryZoneMin = entryZoneMin,
                EntryZoneMax = entryZoneMax,
                Status = ChartSenseStatus.Watching,
                TimeoutAt = timeoutAt,
                SignalId = signal.SignalId,
                TelegramMessageId = signal.TelegramMessageId,
                ProviderChannelId = signal.ProviderChannelId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Calculate Deriv expiry based on timeframe and classification
            setup.DerivExpiryMinutes = CalculateDerivExpiry(signal.Timeframe, classification);

            // Save to database
            var setupId = await _repository.CreateChartSenseSetupAsync(setup);
            setup.SetupId = setupId;

            _logger.LogInformation("📝 Created ChartSense setup #{SetupId}: {Asset} {Direction} @ {Entry} (timeout: {Timeout})",
                setupId, signal.Asset, signal.Direction, signal.EntryPrice, timeoutAt);

            return setup;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating ChartSense setup for signal #{SignalId}", signal.SignalId);
            return null;
        }
    }

    private async Task<CTraderOrderResult> PlacePendingOrderAsync(ParsedSignal signal, ChartSenseSetup setup)
    {
        try
        {
            // Create a signal copy without SL/TP (ChartSense runs without protective orders)
            var orderSignal = new ParsedSignal
            {
                SignalId = signal.SignalId,
                Asset = signal.Asset,
                Direction = signal.Direction,
                EntryPrice = signal.EntryPrice,
                StopLoss = null,  // No SL for ChartSense
                TakeProfit = null,  // No TP for ChartSense
                LotSize = signal.LotSize ?? 0.01m,  // Default lot size
                ProviderChannelId = signal.ProviderChannelId,
                ProviderName = signal.ProviderName ?? "ChartSense",
                SignalType = SignalType.Text,
                ReceivedAt = signal.ReceivedAt,
                RawMessage = signal.RawMessage,
                Pattern = signal.Pattern,
                Timeframe = signal.Timeframe
            };

            return await _pendingOrderService.ProcessSignalAsync(orderSignal, isOpposite: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error placing ChartSense pending order for {Asset}", signal.Asset);
            return new CTraderOrderResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task SendSetupNotificationAsync(ChartSenseSetup setup, string action)
    {
        try
        {
            var message = action switch
            {
                "created" => $"🖼️ ChartSense Setup Created\n" +
                            $"Asset: {setup.Asset}\n" +
                            $"Direction: {setup.Direction}\n" +
                            $"Pattern: {setup.PatternType} ({setup.PatternClassification})\n" +
                            $"Entry: {setup.EntryPrice}\n" +
                            $"Timeframe: {setup.Timeframe}\n" +
                            $"Timeout: {setup.TimeoutAt:HH:mm} UTC",

                _ when action.StartsWith("invalidated") => $"⚡ ChartSense Setup Invalidated\n" +
                            $"Asset: {setup.Asset}\n" +
                            $"Direction: {setup.Direction}\n" +
                            $"Reason: {action.Replace("invalidated (", "").TrimEnd(')')}",

                "expired" => $"⏰ ChartSense Setup Expired\n" +
                            $"Asset: {setup.Asset}\n" +
                            $"Direction: {setup.Direction}\n" +
                            $"Pattern: {setup.PatternType}",

                "filled" => $"✅ ChartSense Order Filled\n" +
                            $"Asset: {setup.Asset}\n" +
                            $"Direction: {setup.Direction}\n" +
                            $"PositionId: {setup.CTraderPositionId}",

                _ => $"🖼️ ChartSense: {setup.Asset} {setup.Direction} - {action}"
            };

            await _notifier.SendTradeMessageAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send ChartSense notification");
        }
    }

    private static int GetTimeoutHours(string? timeframe)
    {
        if (string.IsNullOrEmpty(timeframe))
            return 10; // Default to H1 timeout

        return TimeoutHoursByTimeframe.TryGetValue(timeframe, out var hours) ? hours : 10;
    }

    private static ChartSensePatternClassification DeterminePatternClassification(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return ChartSensePatternClassification.Reaction;

        // Breakout patterns
        var breakoutKeywords = new[] { "breakout", "break", "momentum", "continuation" };
        if (breakoutKeywords.Any(k => pattern.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return ChartSensePatternClassification.Breakout;

        // Everything else is a reaction pattern
        return ChartSensePatternClassification.Reaction;
    }

    private static decimal GetEntryBuffer(string asset)
    {
        // XAUUSD (Gold) gets larger buffer
        if (asset.Contains("XAU", StringComparison.OrdinalIgnoreCase) ||
            asset.Contains("GOLD", StringComparison.OrdinalIgnoreCase))
        {
            return 0.50m; // 50 cents
        }

        // JPY pairs have different pip size
        if (asset.EndsWith("JPY", StringComparison.OrdinalIgnoreCase))
        {
            return 0.05m; // 5 pips for JPY
        }

        // Standard forex pairs
        return 0.0005m; // 5 pips
    }

    private static int CalculateDerivExpiry(string? timeframe, ChartSensePatternClassification classification)
    {
        // Default expiry mapping (in minutes)
        // Reaction patterns get shorter expiry, Breakout patterns get longer
        var baseExpiry = timeframe?.ToUpperInvariant() switch
        {
            "M15" or "15M" => classification == ChartSensePatternClassification.Reaction ? 10 : 15,
            "M30" or "30M" => classification == ChartSensePatternClassification.Reaction ? 15 : 30,
            "H1" or "1H" => classification == ChartSensePatternClassification.Reaction ? 15 : 30,
            "H2" or "2H" => classification == ChartSensePatternClassification.Reaction ? 30 : 60,
            "H4" or "4H" => classification == ChartSensePatternClassification.Reaction ? 60 : 120,
            "D1" or "1D" or "DAILY" => classification == ChartSensePatternClassification.Reaction ? 120 : 240,
            _ => 30 // Default
        };

        return baseExpiry;
    }
}

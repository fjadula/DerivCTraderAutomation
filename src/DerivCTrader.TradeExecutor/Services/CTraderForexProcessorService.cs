using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using DerivCTrader.Infrastructure.CTrader.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// Background service that processes forex signals from ParsedSignalsQueue
/// and creates pending orders on cTrader.
/// 
/// Flow:
/// 1. Poll ParsedSignalsQueue for unprocessed signals
/// 2. Filter for forex signals (exclude pure binary)
/// 3. Load provider config (TakeOriginal/TakeOpposite)
/// 4. Create pending order(s) on cTrader
/// 5. Mark signal as processed
/// 6. CTraderPendingOrderService monitors execution
/// 7. On execution -> writes to TradeExecutionQueue
/// </summary>
public class CTraderForexProcessorService : BackgroundService
{
    private readonly ILogger<CTraderForexProcessorService> _logger;
    private readonly ITradeRepository _repository;
    private readonly ICTraderPendingOrderService _pendingOrderService;
    private readonly IConfiguration _configuration;
    private readonly int _pollIntervalSeconds;

    public CTraderForexProcessorService(
        ILogger<CTraderForexProcessorService> logger,
        ITradeRepository repository,
        ICTraderPendingOrderService pendingOrderService,
        IConfiguration configuration)
    {
        _logger = logger;
        _repository = repository;
        _pendingOrderService = pendingOrderService;
        _configuration = configuration;
        _pollIntervalSeconds = configuration.GetValue("CTrader:SignalPollIntervalSeconds", 5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== CTRADER FOREX PROCESSOR SERVICE STARTED ===");
        Console.WriteLine("========================================");
        Console.WriteLine("  cTrader Forex Signal Processor");
        Console.WriteLine("========================================");
        Console.WriteLine($"📊 Poll Interval: {_pollIntervalSeconds}s");
        Console.WriteLine("🔍 Monitoring ParsedSignalsQueue for forex signals...");
        Console.WriteLine();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessForexSignalsAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cTrader forex processing loop");
                Console.WriteLine($"❌ ERROR: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _logger.LogInformation("=== CTRADER FOREX PROCESSOR SERVICE STOPPED ===");
    }

    private async Task ProcessForexSignalsAsync(CancellationToken cancellationToken)
    {
        // Get all unprocessed signals
        var signals = await _repository.GetUnprocessedSignalsAsync();

        if (signals.Count == 0)
            return;

        // Filter for forex signals only (exclude pure binary)
        var forexSignals = signals
            .Where(s => s.SignalType != SignalType.PureBinary)
            .ToList();

        if (forexSignals.Count == 0)
            return;

        _logger.LogInformation("📋 Found {Count} unprocessed forex signals", forexSignals.Count);
        Console.WriteLine($"📋 Processing {forexSignals.Count} forex signal(s)...");

        foreach (var signal in forexSignals)
        {
            try
            {
                await ProcessSingleForexSignalAsync(signal, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process forex signal {SignalId}", signal.SignalId);
                Console.WriteLine($"❌ Failed signal #{signal.SignalId}: {ex.Message}");
                
                // Mark as processed even on failure to avoid infinite retry
                await _repository.MarkSignalAsProcessedAsync(signal.SignalId);
            }
        }
    }

    private async Task ProcessSingleForexSignalAsync(ParsedSignal signal, CancellationToken cancellationToken)
    {
        _logger.LogInformation("🔨 Processing forex signal #{SignalId}: {Asset} {Direction}", 
            signal.SignalId, signal.Asset, signal.Direction);
        Console.WriteLine($"🔨 Signal #{signal.SignalId}: {signal.Asset} {signal.Direction} @ {signal.EntryPrice}");

        // Load provider configuration
        var providerConfig = await _repository.GetProviderConfigAsync(signal.ProviderChannelId);
        
        if (providerConfig == null)
        {
            _logger.LogWarning("No provider config found for channel {ChannelId}, using defaults", 
                signal.ProviderChannelId);
            
            // Default: TakeOriginal only
            providerConfig = new ProviderChannelConfig
            {
                ProviderChannelId = signal.ProviderChannelId,
                TakeOriginal = true,
                TakeOpposite = false
            };
        }

        var ordersCreated = 0;

        // Create original direction order
        if (providerConfig.TakeOriginal)
        {
            _logger.LogInformation("📝 Creating ORIGINAL direction order: {Direction}", signal.Direction);
            Console.WriteLine($"   📝 Creating ORIGINAL order ({signal.Direction})...");
            
            var success = await _pendingOrderService.ProcessSignalAsync(signal, isOpposite: false);
            
            if (success)
            {
                ordersCreated++;
                _logger.LogInformation("✅ Original order created successfully");
                Console.WriteLine("   ✅ Original order placed");
            }
            else
            {
                _logger.LogWarning("⚠️ Failed to create original direction order");
                Console.WriteLine("   ⚠️ Original order failed");
            }
        }

        // Create opposite direction order
        if (providerConfig.TakeOpposite)
        {
            var oppositeDirection = signal.Direction == TradeDirection.Buy 
                ? TradeDirection.Sell 
                : TradeDirection.Buy;
            
            _logger.LogInformation("📝 Creating OPPOSITE direction order: {Direction}", oppositeDirection);
            Console.WriteLine($"   📝 Creating OPPOSITE order ({oppositeDirection})...");
            
            var success = await _pendingOrderService.ProcessSignalAsync(signal, isOpposite: true);
            
            if (success)
            {
                ordersCreated++;
                _logger.LogInformation("✅ Opposite order created successfully");
                Console.WriteLine("   ✅ Opposite order placed");
            }
            else
            {
                _logger.LogWarning("⚠️ Failed to create opposite direction order");
                Console.WriteLine("   ⚠️ Opposite order failed");
            }
        }

        // Mark signal as processed
        await _repository.MarkSignalAsProcessedAsync(signal.SignalId);
        
        _logger.LogInformation("✅ Forex signal #{SignalId} processed - {Count} order(s) created", 
            signal.SignalId, ordersCreated);
        Console.WriteLine($"✅ Signal #{signal.SignalId} complete - {ordersCreated} pending order(s) on cTrader");
        Console.WriteLine();
    }
}

using System.Globalization;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Infrastructure.Deriv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// Background service that processes TradeExecutionQueue and executes Deriv binary options.
/// 
/// Flow:
/// 1. Poll TradeExecutionQueue for pending entries (written by cTrader after order execution)
/// 2. For each entry:
///    - Calculate expiry (15min or 30min based on asset)
///    - Map direction (Buy → CALL, Sell → PUT)
///    - Execute binary option on Deriv
///    - Log success (KhulaFxTradeMonitor will detect and match)
/// 3. Delete queue entry after successful Deriv execution
/// 
/// NOTE: This service does NOT write to BinaryOptionTrades - that's KhulaFxTradeMonitor's job.
/// We only execute the binary and let KhulaFxTM detect it, match with queue, and update DB.
/// </summary>
public class DerivBinaryExecutorService : BackgroundService
{
    private readonly ILogger<DerivBinaryExecutorService> _logger;
    private readonly ITradeRepository _repository;
    private readonly IDerivClient _derivClient;
    private readonly IBinaryExpiryCalculator _expiryCalculator;
    private readonly decimal _defaultStake;
    private readonly int _pollIntervalSeconds;

    public DerivBinaryExecutorService(
        ILogger<DerivBinaryExecutorService> logger,
        ITradeRepository repository,
        IDerivClient derivClient,
        IBinaryExpiryCalculator expiryCalculator,
        IConfiguration configuration)
    {
        _logger = logger;
        _repository = repository;
        _derivClient = derivClient;
        _expiryCalculator = expiryCalculator;
        _defaultStake = decimal.Parse(
            configuration["BinaryOptions:DefaultStake"] ?? "20",
            CultureInfo.InvariantCulture);
        _pollIntervalSeconds = configuration.GetValue("BinaryOptions:QueuePollIntervalSeconds", 3);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== DERIV BINARY EXECUTOR SERVICE STARTED ===");
        Console.WriteLine("========================================");
        Console.WriteLine("  Deriv Binary Executor (Queue Mode)");
        Console.WriteLine("========================================");
        Console.WriteLine($"💰 Default Stake: ${_defaultStake}");
        Console.WriteLine($"📊 Poll Interval: {_pollIntervalSeconds}s");
        Console.WriteLine("🔍 Monitoring TradeExecutionQueue for cTrader executions...");
        Console.WriteLine();

        // Connect and authorize with Deriv (with retry)
        var connected = false;
        var retryCount = 0;
        var maxRetries = 3;

        while (!connected && retryCount < maxRetries && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                retryCount++;
                _logger.LogInformation("Connecting to Deriv (attempt {Retry}/{Max})...", retryCount, maxRetries);
                
                await _derivClient.ConnectAsync(stoppingToken);
                await Task.Delay(1000, stoppingToken); // Small delay between connect and authorize
                await _derivClient.AuthorizeAsync(stoppingToken);
                
                connected = true;
                _logger.LogInformation("✅ Connected and authorized with Deriv");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to Deriv (attempt {Retry}/{Max})", retryCount, maxRetries);
                
                if (retryCount < maxRetries)
                {
                    var delaySeconds = retryCount * 5;
                    Console.WriteLine($"⏳ Retrying in {delaySeconds} seconds...");
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
                }
                else
                {
                    _logger.LogError(ex, "Failed to connect to Deriv after {MaxRetries} attempts - service cannot start", maxRetries);
                    Console.WriteLine($"❌ FATAL: Cannot connect to Deriv after {maxRetries} attempts: {ex.Message}");
                    return;
                }
            }
        }

        if (!connected)
        {
            _logger.LogError("Service cancelled before connecting to Deriv");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessTradeExecutionQueueAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Deriv binary execution loop");
                Console.WriteLine($"❌ ERROR: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        await _derivClient.DisconnectAsync();
        _logger.LogInformation("=== DERIV BINARY EXECUTOR SERVICE STOPPED ===");
    }

    private async Task ProcessTradeExecutionQueueAsync(CancellationToken cancellationToken)
    {
        var queueEntries = await _repository.GetPendingDerivTradesAsync();

        if (queueEntries.Count == 0)
            return;

        _logger.LogInformation("📋 Found {Count} pending cTrader executions in queue", queueEntries.Count);
        Console.WriteLine($"📋 Processing {queueEntries.Count} cTrader execution(s) from queue...");

        foreach (var queueEntry in queueEntries)
        {
            try
            {
                await ProcessQueueEntryAsync(queueEntry, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process queue entry {QueueId}", queueEntry.QueueId);
                Console.WriteLine($"❌ Failed queue entry #{queueEntry.QueueId}: {ex.Message}");
                
                // Don't delete on failure - retry next poll
                // Consider adding retry count/timestamp logic if needed
            }
        }
    }

    private async Task ProcessQueueEntryAsync(TradeExecutionQueue queueEntry, CancellationToken cancellationToken)
    {
        _logger.LogInformation("🔨 Processing queue entry #{QueueId}: {Asset} {Direction} (cTrader OrderId: {OrderId})",
            queueEntry.QueueId, queueEntry.Asset, queueEntry.Direction, queueEntry.CTraderOrderId);
        Console.WriteLine($"🔨 Queue #{queueEntry.QueueId}: {queueEntry.Asset} {queueEntry.Direction}");
        Console.WriteLine($"   cTrader OrderId: {queueEntry.CTraderOrderId}");
        Console.WriteLine($"   Strategy: {queueEntry.StrategyName}");

        // Calculate expiry based on asset type
        int expiryMinutes = _expiryCalculator.CalculateExpiry("forex", queueEntry.Asset ?? "");
        _logger.LogInformation("📅 Calculated expiry: {Expiry} minutes for {Asset}", expiryMinutes, queueEntry.Asset);
        Console.WriteLine($"   📅 Expiry: {expiryMinutes} minutes");

        // Map direction: Buy → CALL, Sell → PUT
        string derivDirection = MapDirection(queueEntry.Direction ?? "");
        _logger.LogInformation("🎯 Mapped direction: {CTraderDir} → {DerivDir}", queueEntry.Direction, derivDirection);
        Console.WriteLine($"   🎯 Direction: {queueEntry.Direction} → {derivDirection}");

        // Execute binary option on Deriv
        _logger.LogInformation("💳 Executing Deriv binary: {Asset} {Direction} {Expiry}m @ ${Stake}",
            queueEntry.Asset, derivDirection, expiryMinutes, _defaultStake);
        Console.WriteLine($"   💳 Executing Deriv binary: ${_defaultStake} stake...");

        var result = await _derivClient.PlaceBinaryOptionAsync(
            queueEntry.Asset ?? "",
            derivDirection,
            _defaultStake,
            expiryMinutes,
            cancellationToken);

        if (!result.Success)
        {
            _logger.LogError("❌ Deriv execution failed: {Error}", result.ErrorMessage);
            Console.WriteLine($"   ❌ Deriv execution FAILED: {result.ErrorMessage}");
            throw new Exception($"Deriv execution failed: {result.ErrorMessage}");
        }

        _logger.LogInformation("✅ Deriv binary executed successfully");
        Console.WriteLine($"   ✅ Deriv binary executed!");
        Console.WriteLine($"   📝 Contract ID: {result.ContractId}");
        Console.WriteLine($"   💰 Purchase Price: ${result.PurchasePrice}");
        Console.WriteLine($"   🎁 Potential Payout: ${result.Payout}");

        // Delete queue entry (KhulaFxTradeMonitor will detect and match)
        await _repository.DeleteQueueItemAsync(queueEntry.QueueId);
        _logger.LogInformation("🗑️ Deleted queue entry #{QueueId} - waiting for KhulaFxTM to detect and match", queueEntry.QueueId);
        Console.WriteLine($"   🗑️ Queue entry deleted");
        Console.WriteLine($"   ⏳ Waiting for KhulaFxTradeMonitor to detect and match...");
        Console.WriteLine();
    }

    private string MapDirection(string cTraderDirection)
    {
        // cTrader uses: Buy/Sell
        // Deriv uses: CALL/PUT
        return cTraderDirection.ToUpper() switch
        {
            "BUY" => "CALL",
            "SELL" => "PUT",
            _ => throw new ArgumentException($"Unknown direction: {cTraderDirection}")
        };
    }
}

using System.Globalization;
using System.Collections.Concurrent;
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
/// 3. Update queue entry with DerivContractId (do NOT delete; KhulaFxTradeMonitor handles cleanup)
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
    private readonly System.Threading.SemaphoreSlim _wakeSemaphore = new(0, 1);

    // Synthetic-safe execution: queue and semaphore
    private readonly ConcurrentQueue<TradeExecutionQueue> _syntheticQueue = new();
    private readonly SemaphoreSlim _syntheticSemaphore = new(1, 1);

    // Expose a method for external event to trigger processing
    public void WakeUp() => _wakeSemaphore.Release();

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
        Console.WriteLine($"📊 Fallback Poll: {_pollIntervalSeconds}s (notification-driven)");
        Console.WriteLine("🔔 Waiting for SQL notifications from TradeExecutionQueue...");
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
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to Deriv (attempt {Retry}/{Max})", retryCount, maxRetries);
                
                if (retryCount < maxRetries)
                {
                    var delaySeconds = retryCount * 5;
                    Console.WriteLine($"⏳ Retrying in {delaySeconds} seconds...");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
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

        // Process any pending entries on startup (in case queue has items from before service started)
        try
        {
            _logger.LogInformation("[STARTUP] Processing any pending queue entries from before service start");
            await ProcessTradeExecutionQueueAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing startup queue - will retry on next notification");
        }

        // Main loop: wait for notification FIRST, then process
        // This is more efficient than polling every N seconds
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for either a wake-up notification or poll interval (fallback for reliability)
                _logger.LogDebug("[WAIT] Waiting for SQL notification or {PollInterval}s timeout...", _pollIntervalSeconds);
                var delayTask = Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), stoppingToken);
                var wakeTask = _wakeSemaphore.WaitAsync(stoppingToken);
                var completed = await Task.WhenAny(delayTask, wakeTask);

                if (completed == wakeTask)
                {
                    _logger.LogInformation("[WAKE] Received notification - processing queue immediately");
                }
                else
                {
                    _logger.LogDebug("[POLL] Fallback poll interval reached - checking queue");
                }

                // Now process the queue
                await ProcessTradeExecutionQueueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Deriv binary execution loop");
                Console.WriteLine($"❌ ERROR: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
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

        // Partition: synthetics (volatility indices) vs. others
        var normalEntries = new List<TradeExecutionQueue>();
        foreach (var entry in queueEntries)
        {
            if (IsSyntheticAsset(entry.Asset))
            {
                _syntheticQueue.Enqueue(entry);
            }
            else
            {
                normalEntries.Add(entry);
            }
        }

        // Process normal entries immediately (parallel allowed)
        foreach (var queueEntry in normalEntries)
        {
            try
            {
                await ProcessQueueEntryAsync(queueEntry, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process queue entry {QueueId}", queueEntry.QueueId);
                Console.WriteLine($"❌ Failed queue entry #{queueEntry.QueueId}: {ex.Message}");
            }
        }

        // Process synthetics serially (one at a time)
        if (!_syntheticQueue.IsEmpty)
        {
            await _syntheticSemaphore.WaitAsync(cancellationToken);
            try
            {
                while (_syntheticQueue.TryDequeue(out var syntheticEntry))
                {
                    try
                    {
                        await ProcessQueueEntryAsync(syntheticEntry, cancellationToken, isSynthetic:true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[SYNTHETIC] Failed to process queue entry {QueueId}", syntheticEntry.QueueId);
                        Console.WriteLine($"❌ [SYNTHETIC] Failed queue entry #{syntheticEntry.QueueId}: {ex.Message}");
                    }
                }
            }
            finally
            {
                _syntheticSemaphore.Release();
            }
        }
    }

    private async Task ProcessQueueEntryAsync(TradeExecutionQueue queueEntry, CancellationToken cancellationToken, bool isSynthetic = false)
    {
        var now = DateTime.UtcNow.ToString("O");
        _logger.LogInformation("[TIMING] {Now} Processing queue entry #{QueueId}: {Asset} {Direction} (cTrader OrderId: {OrderId})",
            now, queueEntry.QueueId, queueEntry.Asset, queueEntry.Direction, queueEntry.CTraderOrderId);
        Console.WriteLine($"🔨 Queue #{queueEntry.QueueId}: {queueEntry.Asset} {queueEntry.Direction}");
        Console.WriteLine($"   cTrader OrderId: {queueEntry.CTraderOrderId}");
        Console.WriteLine($"   Strategy: {queueEntry.StrategyName}");

        // Calculate expiry - provider-specific first, then fallback to asset-based
        int expiryMinutes = GetProviderExpiry(queueEntry.StrategyName)
            ?? _expiryCalculator.CalculateExpiry("forex", queueEntry.Asset ?? "");
        _logger.LogInformation("📅 Calculated expiry: {Expiry} minutes for {Asset} (Provider: {Provider})",
            expiryMinutes, queueEntry.Asset, queueEntry.StrategyName ?? "default");
        Console.WriteLine($"   📅 Expiry: {expiryMinutes} minutes");

        // Map direction: Buy → CALL, Sell → PUT
        string derivDirection = MapDirection(queueEntry.Direction ?? "");
        _logger.LogInformation("🎯 Mapped direction: {CTraderDir} → {DerivDir}", queueEntry.Direction, derivDirection);
        Console.WriteLine($"   🎯 Direction: {queueEntry.Direction} → {derivDirection}");

        // Execute binary option on Deriv
        _logger.LogInformation("💳 Executing Deriv binary: {Asset} {Direction} {Expiry}m @ ${Stake}",
            queueEntry.Asset, derivDirection, expiryMinutes, _defaultStake);
        Console.WriteLine($"   💳 Executing Deriv binary: ${_defaultStake} stake...");

        DerivTradeResult result = null;
        int maxRetries = isSynthetic ? 3 : 1;
        int attempt = 0;
        Exception lastEx = null;
        while (attempt < maxRetries)
        {
            attempt++;
            try
            {
                result = await _derivClient.PlaceBinaryOptionAsync(
                    queueEntry.Asset ?? "",
                    derivDirection,
                    _defaultStake,
                    expiryMinutes,
                    cancellationToken);
                if (result.Success)
                    break;
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
            if (attempt < maxRetries)
            {
                _logger.LogWarning("[SYNTHETIC] Attempt {Attempt}/{Max} failed for asset {Asset}. Retrying...", attempt, maxRetries, queueEntry.Asset);
                await Task.Delay(1000, cancellationToken);
            }
        }

        // Fallback: for synthetics, if still failed, try market order (no expiry override, just log)
        if (isSynthetic && (result == null || !result.Success))
        {
            _logger.LogWarning("[SYNTHETIC] All retries failed for asset {Asset}. Attempting fallback market order...", queueEntry.Asset);
            // Optionally, you could adjust expiry or stake here for fallback
            try
            {
                result = await _derivClient.PlaceBinaryOptionAsync(
                    queueEntry.Asset ?? "",
                    derivDirection,
                    _defaultStake,
                    expiryMinutes,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
        }


        if (result == null || !result.Success)
        {
            _logger.LogError("❌ Deriv execution failed: {Error}", result?.ErrorMessage ?? lastEx?.Message);
            Console.WriteLine($"   ❌ Deriv execution FAILED: {result?.ErrorMessage ?? lastEx?.Message}");
            throw new Exception($"Deriv execution failed: {result?.ErrorMessage ?? lastEx?.Message}");
        }

        var nowOrder = DateTime.UtcNow.ToString("O");
        _logger.LogInformation("[TIMING] {Now} ✅ Deriv binary executed successfully", nowOrder);
        Console.WriteLine($"   ✅ Deriv binary executed!");
        Console.WriteLine($"   📝 Contract ID: {result.ContractId}");
        Console.WriteLine($"   💰 Purchase Price: ${result.PurchasePrice}");
        Console.WriteLine($"   🎁 Potential Payout: ${result.Payout}");

        // IMPORTANT: Do NOT delete from TradeExecutionQueue.
        // KhulaFxTradeMonitor depends on the row existing to match FIFO by (Asset, Direction).
        await _repository.UpdateTradeExecutionQueueDerivContractAsync(queueEntry.QueueId, result.ContractId ?? string.Empty);
        _logger.LogInformation("🧾 Updated queue entry #{QueueId} with DerivContractId={ContractId} (left in queue for KhulaFxTM)", queueEntry.QueueId, result.ContractId);
        Console.WriteLine($"   🧾 Queue entry updated (not deleted)");
        Console.WriteLine($"   ⏳ Waiting for KhulaFxTradeMonitor to detect and match...");
        Console.WriteLine();
    }

    // Helper: Detect if asset is a synthetic/volatility index
    private bool IsSyntheticAsset(string asset)
    {
        if (string.IsNullOrWhiteSpace(asset))
            return false;
        // Use expiry calculator's logic for volatility indices
        return asset.Contains("Volatility", StringComparison.OrdinalIgnoreCase)
            || asset.Contains("VIX", StringComparison.OrdinalIgnoreCase)
            || asset.Contains("HZ", StringComparison.OrdinalIgnoreCase);
    }

    private string MapDirection(string direction)
    {
        // Handle both cTrader directions (Buy/Sell) and pure binary directions (CALL/PUT)
        // cTrader uses: Buy/Sell
        // Deriv uses: CALL/PUT
        return direction.ToUpper() switch
        {
            "BUY" => "CALL",
            "SELL" => "PUT",
            "CALL" => "CALL",   // Already in Deriv format (from pure binary signals)
            "PUT" => "PUT",     // Already in Deriv format (from pure binary signals)
            _ => throw new ArgumentException($"Unknown direction: {direction}")
        };
    }

    /// <summary>
    /// Provider-specific expiry settings (in minutes)
    /// </summary>
    private static int? GetProviderExpiry(string? providerName)
    {
        if (string.IsNullOrEmpty(providerName))
            return null;

        return providerName.ToUpperInvariant() switch
        {
            "PIPSMOVE" => 45,                    // 45 minutes
            "FXTRADINGPROFESSOR" => 45,          // 45 minutes
            "PERFECTFX" => 960,                  // 16 hours
            "VIP KNIGHTS" => 240,                // 4 hours
            "AFXGOLD" => 40,                     // 40 minutes
            _ => null
        };
    }
}

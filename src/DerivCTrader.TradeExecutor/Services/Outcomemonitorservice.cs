using DerivCTrader.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// Monitors open Deriv trades and updates outcomes after expiry
/// </summary>
public class OutcomeMonitorService : BackgroundService
{
    private readonly ILogger<OutcomeMonitorService> _logger;
    private readonly ITradeRepository _repository;
    private readonly IDerivClient _derivClient;
    private readonly int _checkIntervalSeconds;

    public OutcomeMonitorService(
        ILogger<OutcomeMonitorService> logger,
        ITradeRepository repository,
        IDerivClient derivClient,
        IConfiguration configuration)
    {
        _logger = logger;
        _repository = repository;
        _derivClient = derivClient;
        
        _checkIntervalSeconds = int.Parse(configuration["OutcomeMonitor:CheckIntervalSeconds"] ?? "30");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== OUTCOME MONITOR SERVICE STARTING ===");
        Console.WriteLine("=== OUTCOME MONITOR SERVICE STARTING ===");

        // Wait for Deriv client to connect
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckPendingTradesAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(_checkIntervalSeconds), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in outcome monitor loop");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _logger.LogInformation("=== OUTCOME MONITOR SERVICE STOPPED ===");
    }

    private async Task CheckPendingTradesAsync(CancellationToken cancellationToken)
    {
        var pendingTrades = await _repository.GetPendingDerivTradesAsync();

        if (pendingTrades.Count == 0)
            return;

        _logger.LogDebug("Checking {Count} pending Deriv trades", pendingTrades.Count);

        foreach (var trade in pendingTrades)
        {
            try
            {
                // Check if expired
                var expiryTime = trade.CreatedAt.AddMinutes(trade.ExpiryMinutes ?? 21);
                
                if (DateTime.UtcNow < expiryTime)
                    continue;

                // Get outcome from Deriv
                if (string.IsNullOrEmpty(trade.DerivContractId))
                    continue;

                var outcome = await _derivClient.GetContractOutcomeAsync(trade.DerivContractId, cancellationToken);

                if (outcome == null)
                {
                    _logger.LogWarning("Could not get outcome for contract {ContractId}", trade.DerivContractId);
                    continue;
                }

                // Update in database
                await _repository.UpdateDerivTradeOutcomeAsync(
                    trade.QueueId,
                    outcome.Status,
                    outcome.Profit);

                _logger.LogInformation("🎉 TRADE SETTLED: {Asset} {Direction} {Status} ${Profit}",
                    trade.Asset, trade.Direction, outcome.Status, outcome.Profit);
                Console.WriteLine($"🎉 SETTLED: {trade.Asset} {trade.Direction} {outcome.Status} ${outcome.Profit}");

                // Log balance
                try
                {
                    var balance = await _derivClient.GetBalanceAsync(cancellationToken);
                    Console.WriteLine($"💰 Balance: ${balance}");
                }
                catch { }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check outcome for trade {QueueId}", trade.QueueId);
            }
        }
    }
}
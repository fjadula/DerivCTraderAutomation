using DerivCTrader.Application.Interfaces;
using DerivCTrader.Infrastructure.Deriv;
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
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckPendingTradesAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(_checkIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in outcome monitor loop");
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

        _logger.LogInformation("=== OUTCOME MONITOR SERVICE STOPPED ===");
    }

    private async Task CheckPendingTradesAsync(CancellationToken cancellationToken)
    {
        // Get pending Deriv trades (from BinaryOptionTrade via TradeExecutionQueue)
        // TradeExecutionQueue is the matching queue between cTrader and Deriv
        var pendingTrades = await _repository.GetPendingDerivTradesAsync();

        if (pendingTrades.Count == 0)
            return;

        _logger.LogDebug("Checking {Count} pending Deriv trades", pendingTrades.Count);

        foreach (var trade in pendingTrades)
        {
            try
            {
                // Check if expired
                var expiryTime = trade.CreatedAt.AddMinutes(15); // Default wait before checking outcome
                
                if (DateTime.UtcNow < expiryTime)
                    continue;

                // TODO: Get outcome from Deriv API
                // This requires contract ID to be stored in TradeExecutionQueue or linked table
                // var outcome = await _derivClient.GetContractOutcomeAsync(trade.ContractId, cancellationToken);

                // For now, mark as checked
                _logger.LogInformation("⏳ Trade expired and pending outcome verification: {Asset} {Direction}",
                    trade.Asset, trade.Direction);

                // TODO: Update trade result when Deriv API integration complete
                // await _repository.UpdateDerivTradeOutcomeAsync(
                //     trade.QueueId,
                //     outcome.Status ?? "Unknown",
                //     outcome.Profit);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check outcome for trade {QueueId}", trade.QueueId);
            }
        }
    }
}
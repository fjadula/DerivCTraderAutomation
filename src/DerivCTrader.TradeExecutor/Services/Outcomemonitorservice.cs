using DerivCTrader.Application.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// Background service that monitors open Deriv trades and updates outcomes after expiry
/// </summary>
public class OutcomeMonitorService : BackgroundService
{
    private readonly IDerivClient _derivClient;
    private readonly ITradeRepository _repository;
    private readonly ILogger<OutcomeMonitorService> _logger;

    public OutcomeMonitorService(
        IDerivClient derivClient,
        ITradeRepository repository,
        ILogger<OutcomeMonitorService> logger)
    {
        _derivClient = derivClient;
        _repository = repository;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outcome Monitor Service starting...");
        Console.WriteLine("=== OUTCOME MONITOR SERVICE STARTING ===");

        // Wait a bit for BinaryExecutionService to connect first
        await Task.Delay(5000, stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckOpenTradesAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error checking trades");
                }

                // Check every 10 seconds
                await Task.Delay(10000, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Outcome Monitor Service stopping...");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in Outcome Monitor Service");
        }
        finally
        {
            _logger.LogInformation("Outcome Monitor Service stopped");
        }
    }

    private async Task CheckOpenTradesAsync(CancellationToken cancellationToken)
    {
        var openTrades = await _repository.GetOpenDerivTradesAsync();

        if (openTrades.Count == 0)
            return;

        _logger.LogDebug("Checking {Count} open trades", openTrades.Count);

        foreach (var trade in openTrades)
        {
            try
            {
                // Calculate expiry time
                var expiryTime = trade.PurchasedAt.AddMinutes(trade.Expiry);

                // Check if trade has expired
                if (DateTime.UtcNow < expiryTime)
                {
                    var remaining = (expiryTime - DateTime.UtcNow).TotalSeconds;
                    _logger.LogDebug("Trade {ContractId} expires in {Seconds}s",
                        trade.ContractId, (int)remaining);
                    continue;
                }

                // Trade has expired - get outcome
                _logger.LogInformation("⏰ Trade {ContractId} expired, checking outcome...",
                    trade.ContractId);

                var outcome = await _derivClient.GetContractOutcomeAsync(
                    trade.ContractId,
                    cancellationToken);

                // Update database
                await _repository.UpdateDerivTradeOutcomeAsync(
                    trade.TradeId,
                    outcome.Status,
                    outcome.Profit);

                // Log result
                var emoji = outcome.Status == "Win" ? "🎉" : "😞";
                _logger.LogInformation("{Emoji} Trade {ContractId} settled: {Status} {Profit:C}",
                    emoji, trade.ContractId, outcome.Status, outcome.Profit);

                Console.WriteLine($"{emoji} TRADE SETTLED: {trade.Asset} {trade.Direction} " +
                                $"{outcome.Status} ${outcome.Profit:F2}");

                // Calculate and display statistics
                await DisplayStatisticsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check trade {ContractId}", trade.ContractId);
            }
        }
    }

    private async Task DisplayStatisticsAsync()
    {
        try
        {
            // Get all settled trades (you could add repository method for this)
            // For now, just get balance
            if (_derivClient.IsConnected && _derivClient.IsAuthorized)
            {
                var balance = await _derivClient.GetBalanceAsync();
                _logger.LogInformation("💰 Current Balance: ${Balance}", balance);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get statistics");
        }
    }
}
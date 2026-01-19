using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Enums;
using DerivCTrader.Infrastructure.CTrader.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// Background service that monitors ChartSense setups for timeout expiry.
///
/// When a setup's TimeoutAt is reached:
/// 1. If PendingPlaced: Cancel the pending cTrader order
/// 2. Mark setup as Expired
/// 3. Send notification
/// </summary>
public class ChartSenseTimeoutMonitor : BackgroundService
{
    private readonly ILogger<ChartSenseTimeoutMonitor> _logger;
    private readonly ITradeRepository _repository;
    private readonly ICTraderOrderManager _orderManager;
    private readonly ITelegramNotifier _notifier;
    private readonly int _checkIntervalSeconds;

    public ChartSenseTimeoutMonitor(
        ILogger<ChartSenseTimeoutMonitor> logger,
        ITradeRepository repository,
        ICTraderOrderManager orderManager,
        ITelegramNotifier notifier,
        IConfiguration configuration)
    {
        _logger = logger;
        _repository = repository;
        _orderManager = orderManager;
        _notifier = notifier;
        _checkIntervalSeconds = configuration.GetValue("CTrader:ChartSenseTimeoutCheckSeconds", 60);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== CHARTSENSE TIMEOUT MONITOR STARTED ===");
        Console.WriteLine("========================================");
        Console.WriteLine("  ChartSense Timeout Monitor");
        Console.WriteLine("========================================");
        Console.WriteLine($"⏰ Check Interval: {_checkIntervalSeconds}s");
        Console.WriteLine();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckExpiredSetupsAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(_checkIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ChartSense timeout monitor loop");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("=== CHARTSENSE TIMEOUT MONITOR STOPPED ===");
    }

    private async Task CheckExpiredSetupsAsync(CancellationToken cancellationToken)
    {
        var expiredSetups = await _repository.GetChartSenseSetupsWithExpiredTimeoutAsync();

        if (expiredSetups.Count == 0)
        {
            _logger.LogDebug("No expired ChartSense setups found");
            return;
        }

        _logger.LogInformation("⏰ Found {Count} expired ChartSense setup(s)", expiredSetups.Count);
        Console.WriteLine($"⏰ Processing {expiredSetups.Count} expired ChartSense setup(s)...");

        foreach (var setup in expiredSetups)
        {
            try
            {
                await ProcessExpiredSetupAsync(setup, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing expired setup {SetupId}", setup.SetupId);
            }
        }
    }

    private async Task ProcessExpiredSetupAsync(Domain.Entities.ChartSenseSetup setup, CancellationToken cancellationToken)
    {
        _logger.LogInformation("⏰ Processing expired setup #{SetupId}: {Asset} {Direction} (Status: {Status})",
            setup.SetupId, setup.Asset, setup.Direction, setup.Status);

        Console.WriteLine($"⏰ Expiring setup #{setup.SetupId}: {setup.Asset} {setup.Direction}");

        // If setup has a pending order, cancel it
        if (setup.Status == ChartSenseStatus.PendingPlaced && setup.CTraderOrderId.HasValue)
        {
            _logger.LogInformation("📛 Cancelling expired pending order {OrderId}", setup.CTraderOrderId);
            Console.WriteLine($"   📛 Cancelling order {setup.CTraderOrderId}...");

            try
            {
                var cancelled = await _orderManager.CancelOrderAsync(setup.CTraderOrderId.Value);
                if (cancelled)
                {
                    _logger.LogInformation("✅ Expired order {OrderId} cancelled", setup.CTraderOrderId);
                    Console.WriteLine("   ✅ Order cancelled");
                }
                else
                {
                    _logger.LogWarning("⚠️ Failed to cancel expired order {OrderId}", setup.CTraderOrderId);
                    Console.WriteLine("   ⚠️ Cancel failed (order may already be filled/cancelled)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cancelling order {OrderId}", setup.CTraderOrderId);
            }
        }

        // Mark setup as expired
        setup.Status = ChartSenseStatus.Expired;
        setup.ClosedAt = DateTime.UtcNow;
        setup.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateChartSenseSetupAsync(setup);

        _logger.LogInformation("✅ Setup #{SetupId} marked as Expired", setup.SetupId);
        Console.WriteLine($"   ✅ Setup #{setup.SetupId} expired");

        // Send notification
        await SendExpiryNotificationAsync(setup);
    }

    private async Task SendExpiryNotificationAsync(Domain.Entities.ChartSenseSetup setup)
    {
        try
        {
            var message = $"⏰ ChartSense Setup Expired\n" +
                         $"Asset: {setup.Asset}\n" +
                         $"Direction: {setup.Direction}\n" +
                         $"Pattern: {setup.PatternType}\n" +
                         $"Timeframe: {setup.Timeframe}\n" +
                         $"Created: {setup.CreatedAt:HH:mm} UTC\n" +
                         $"Timeout: {setup.TimeoutAt:HH:mm} UTC";

            await _notifier.SendTradeMessageAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send expiry notification for setup #{SetupId}", setup.SetupId);
        }
    }
}

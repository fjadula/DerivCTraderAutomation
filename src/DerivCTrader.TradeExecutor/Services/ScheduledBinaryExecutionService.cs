using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using DerivCTrader.Infrastructure.Deriv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// Background service that executes scheduled binary signals at their designated times.
/// Used for providers like CMFLIX that publish pre-scheduled signals.
///
/// Unlike poll-based services that execute immediately, this service:
/// 1. Queries for the next scheduled signal
/// 2. Waits until the scheduled time
/// 3. Executes the trade on Deriv
/// 4. Moves to the next scheduled signal
/// </summary>
public class ScheduledBinaryExecutionService : BackgroundService
{
    private readonly ITradeRepository _repository;
    private readonly IDerivClient _derivClient;
    private readonly ITelegramNotifier _telegramNotifier;
    private readonly ILogger<ScheduledBinaryExecutionService> _logger;
    private readonly decimal _stakeUsd;
    private readonly bool _isEnabled;

    public ScheduledBinaryExecutionService(
        ITradeRepository repository,
        IDerivClient derivClient,
        ITelegramNotifier telegramNotifier,
        IConfiguration configuration,
        ILogger<ScheduledBinaryExecutionService> logger)
    {
        _repository = repository;
        _derivClient = derivClient;
        _telegramNotifier = telegramNotifier;
        _logger = logger;

        _stakeUsd = configuration.GetValue<decimal>("Cmflix:StakeUsd", 20);
        _isEnabled = configuration.GetValue<bool>("Cmflix:Enabled", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_isEnabled)
        {
            _logger.LogInformation("ScheduledBinaryExecutionService is disabled via configuration");
            return;
        }

        _logger.LogInformation("ScheduledBinaryExecutionService starting with stake={StakeUsd} USD", _stakeUsd);

        // Connect to Deriv
        try
        {
            await _derivClient.ConnectAsync(stoppingToken);
            await _derivClient.AuthorizeAsync(stoppingToken);
            _logger.LogInformation("Connected and authorized with Deriv");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Deriv. Service will retry on next loop.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // First, process any signals that are already due (catch-up for missed signals)
                await ProcessDueSignalsAsync(stoppingToken);

                // Then, get the next scheduled signal and wait for it
                await WaitAndExecuteNextScheduledSignalAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("ScheduledBinaryExecutionService stopping due to cancellation");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in scheduled binary execution loop");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    /// <summary>
    /// Process any signals that are already due (scheduled time has passed).
    /// This handles catch-up when the service starts late or misses signals.
    /// </summary>
    private async Task ProcessDueSignalsAsync(CancellationToken cancellationToken)
    {
        var dueSignals = await _repository.GetScheduledSignalsDueAsync(DateTime.UtcNow);

        if (dueSignals.Count == 0)
            return;

        _logger.LogInformation("Found {Count} due scheduled signals to process", dueSignals.Count);

        foreach (var signal in dueSignals)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var minutesLate = (DateTime.UtcNow - signal.ScheduledAtUtc!.Value).TotalMinutes;

            // Skip signals that are too old (more than 5 minutes late)
            if (minutesLate > 5)
            {
                _logger.LogWarning(
                    "Skipping signal {SignalId} ({Asset} {Direction}) - {MinutesLate:F1} minutes late",
                    signal.SignalId, signal.Asset, signal.Direction, minutesLate);

                await _repository.MarkSignalAsProcessedAsync(signal.SignalId);
                continue;
            }

            await ExecuteSignalAsync(signal, cancellationToken);
        }
    }

    /// <summary>
    /// Wait for the next scheduled signal and execute it at the right time.
    /// Uses chunked waiting pattern for responsive cancellation.
    /// </summary>
    private async Task WaitAndExecuteNextScheduledSignalAsync(CancellationToken cancellationToken)
    {
        // Get the next scheduled signal (after now)
        var nextSignal = await _repository.GetNextScheduledSignalAsync(DateTime.UtcNow);

        if (nextSignal == null)
        {
            // No pending scheduled signals, check again in 1 minute
            _logger.LogDebug("No pending scheduled signals. Checking again in 1 minute.");
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            return;
        }

        var scheduledTime = nextSignal.ScheduledAtUtc!.Value;
        var waitTime = scheduledTime - DateTime.UtcNow;

        _logger.LogInformation(
            "Next scheduled signal: {Asset} {Direction} at {ScheduledTime:HH:mm:ss} UTC (in {WaitMinutes:F1} minutes)",
            nextSignal.Asset, nextSignal.Direction, scheduledTime, waitTime.TotalMinutes);

        // Wait in 10-second chunks for responsive cancellation
        while (waitTime.TotalSeconds > 10 && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            waitTime = scheduledTime - DateTime.UtcNow;
        }

        // Final precise wait
        if (waitTime.TotalSeconds > 0 && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(waitTime, cancellationToken);
        }

        // Re-fetch the signal to ensure it hasn't been processed by another instance
        var freshSignal = await _repository.GetNextScheduledSignalAsync(DateTime.UtcNow.AddSeconds(-30));
        if (freshSignal == null || freshSignal.SignalId != nextSignal.SignalId)
        {
            _logger.LogDebug("Signal {SignalId} was already processed or no longer exists", nextSignal.SignalId);
            return;
        }

        // Execute the signal
        await ExecuteSignalAsync(nextSignal, cancellationToken);
    }

    private async Task ExecuteSignalAsync(ParsedSignal signal, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "Executing scheduled signal: {Asset} {Direction} (SignalId={SignalId}, Provider={Provider})",
                signal.Asset, signal.Direction, signal.SignalId, signal.ProviderName);

            // Ensure connected
            if (!_derivClient.IsConnected || !_derivClient.IsAuthorized)
            {
                _logger.LogWarning("Deriv client not connected, attempting reconnect...");
                await _derivClient.ConnectAsync(cancellationToken);
                await _derivClient.AuthorizeAsync(cancellationToken);
            }

            // Map direction: Buy/Call -> CALL, Sell/Put -> PUT
            var derivDirection = signal.Direction switch
            {
                TradeDirection.Buy => "CALL",
                TradeDirection.Call => "CALL",
                TradeDirection.Sell => "PUT",
                TradeDirection.Put => "PUT",
                _ => "CALL"
            };

            // Parse expiry from Timeframe (default 15 minutes)
            var expiryMinutes = 15;
            if (!string.IsNullOrEmpty(signal.Timeframe) && int.TryParse(signal.Timeframe, out var parsedExpiry))
            {
                expiryMinutes = parsedExpiry;
            }

            // Execute on Deriv
            var result = await _derivClient.PlaceBinaryOptionAsync(
                signal.Asset,
                derivDirection,
                _stakeUsd,
                expiryMinutes,
                cancellationToken);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Successfully executed {Asset} {Direction} - ContractId={ContractId}, BuyPrice={BuyPrice}",
                    signal.Asset, derivDirection, result.ContractId, result.BuyPrice);

                // Send notification
                await _telegramNotifier.SendTradeMessageAsync(
                    $"📊 CMFLIX Signal Executed\n{signal.Asset} {derivDirection}\nStake: ${_stakeUsd}\nExpiry: {expiryMinutes}m\nContract: {result.ContractId}",
                    cancellationToken);
            }
            else
            {
                _logger.LogError(
                    "Failed to execute {Asset} {Direction}: {ErrorMessage}",
                    signal.Asset, derivDirection, result.ErrorMessage);

                await _telegramNotifier.SendTradeMessageAsync(
                    $"❌ CMFLIX Signal Failed\n{signal.Asset} {derivDirection}\nError: {result.ErrorMessage}",
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Exception executing signal {SignalId} ({Asset} {Direction})",
                signal.SignalId, signal.Asset, signal.Direction);
        }
        finally
        {
            // Always mark as processed to prevent retry loops
            await _repository.MarkSignalAsProcessedAsync(signal.SignalId);
        }
    }
}

using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Infrastructure.Deriv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// Main background service for Dasha Trade selective martingale execution.
///
/// Flow:
/// 1. Poll DashaPendingSignals for signals that have reached expiry
/// 2. Fetch exit price using Deriv tick API
/// 3. Determine if provider lost (compare entry vs exit price)
/// 4. If provider lost: execute on Deriv with current compounding stake
/// 5. Update compounding state based on our execution result
///
/// This service runs in the TradeExecutor and coordinates between:
/// - DashaPendingSignals (from SignalScraper)
/// - DerivClient (for price and execution)
/// - DashaCompoundingManager (for stake management)
/// - DashaTrades (execution records)
/// </summary>
public class DashaTradeExecutionService : BackgroundService
{
    private readonly ILogger<DashaTradeExecutionService> _logger;
    private readonly IDashaTradeRepository _dashaRepository;
    private readonly IDerivClient _derivClient;
    private readonly DashaCompoundingManager _compoundingManager;
    private readonly ITelegramNotifier _notifier;

    private readonly int _pollIntervalSeconds;
    private readonly bool _isEnabled;

    public DashaTradeExecutionService(
        ILogger<DashaTradeExecutionService> logger,
        IDashaTradeRepository dashaRepository,
        IDerivClient derivClient,
        DashaCompoundingManager compoundingManager,
        ITelegramNotifier notifier,
        IConfiguration configuration)
    {
        _logger = logger;
        _dashaRepository = dashaRepository;
        _derivClient = derivClient;
        _compoundingManager = compoundingManager;
        _notifier = notifier;

        _pollIntervalSeconds = configuration.GetValue("DashaTrade:PollIntervalSeconds", 10);
        _isEnabled = configuration.GetValue("DashaTrade:Enabled", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_isEnabled)
        {
            _logger.LogInformation("[DashaExecution] Service is DISABLED via configuration");
            return;
        }

        _logger.LogInformation("[DashaExecution] Starting Dasha Trade Execution Service (poll interval: {Interval}s)",
            _pollIntervalSeconds);

        // Wait for Deriv connection to be established by other services
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // First: Fill in entry prices for newly received signals
                await FillEntryPricesAsync(stoppingToken);

                // Then: Process signals that have reached expiry
                await ProcessPendingSignalsAsync(stoppingToken);

                // Finally: Check outcomes of executed trades
                await ProcessUnsettledTradesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DashaExecution] Error in main loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), stoppingToken);
        }

        _logger.LogInformation("[DashaExecution] Service stopped");
    }

    /// <summary>
    /// Fill in entry prices for newly received signals (EntryPrice = 0).
    /// This runs frequently to capture prices as soon as possible after signal receipt.
    /// </summary>
    private async Task FillEntryPricesAsync(CancellationToken stoppingToken)
    {
        var signalsNeedingPrice = await _dashaRepository.GetSignalsNeedingEntryPriceAsync();

        if (signalsNeedingPrice.Count == 0)
            return;

        _logger.LogInformation("[DashaExecution] Found {Count} signals needing entry price", signalsNeedingPrice.Count);

        // Ensure Deriv is connected
        if (!_derivClient.IsAuthorized)
        {
            try
            {
                await _derivClient.ConnectAsync(stoppingToken);
                await _derivClient.AuthorizeAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DashaExecution] Failed to connect to Deriv for price fetch");
                return;
            }
        }

        foreach (var signal in signalsNeedingPrice)
        {
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                var entryPrice = await _derivClient.GetSpotPriceAsync(signal.Asset, stoppingToken);

                if (entryPrice.HasValue && entryPrice.Value > 0)
                {
                    signal.EntryPrice = entryPrice.Value;
                    await _dashaRepository.UpdatePendingSignalAsync(signal);

                    _logger.LogInformation(
                        "[DashaExecution] Entry price captured for signal {SignalId}: {Asset} @ {Price}",
                        signal.PendingSignalId, signal.Asset, entryPrice.Value);
                }
                else
                {
                    _logger.LogWarning(
                        "[DashaExecution] Failed to get entry price for signal {SignalId}: {Asset}",
                        signal.PendingSignalId, signal.Asset);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DashaExecution] Error fetching entry price for signal {SignalId}", signal.PendingSignalId);
            }
        }
    }

    /// <summary>
    /// Process pending signals that have reached expiry time.
    /// </summary>
    private async Task ProcessPendingSignalsAsync(CancellationToken stoppingToken)
    {
        var pendingSignals = await _dashaRepository.GetSignalsAwaitingEvaluationAsync();

        if (pendingSignals.Count == 0)
            return;

        _logger.LogInformation("[DashaExecution] Found {Count} signals awaiting evaluation", pendingSignals.Count);

        foreach (var signal in pendingSignals)
        {
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                await EvaluateAndExecuteSignalAsync(signal, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DashaExecution] Error processing signal {SignalId}", signal.PendingSignalId);

                // Mark signal as error to prevent infinite retry
                signal.Status = DashaPendingSignalStatus.Error;
                signal.EvaluatedAt = DateTime.UtcNow;
                await _dashaRepository.UpdatePendingSignalAsync(signal);
            }
        }
    }

    /// <summary>
    /// Evaluate a signal at expiry and execute if provider lost.
    /// </summary>
    private async Task EvaluateAndExecuteSignalAsync(DashaPendingSignal signal, CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[DashaExecution] Evaluating signal {SignalId}: {Asset} {Direction} (entry: {EntryPrice})",
            signal.PendingSignalId, signal.Asset, signal.Direction, signal.EntryPrice);

        // Step 1: Ensure Deriv is connected and authorized
        if (!_derivClient.IsAuthorized)
        {
            await _derivClient.ConnectAsync(stoppingToken);
            await _derivClient.AuthorizeAsync(stoppingToken);
        }

        // Step 2: Fetch exit price
        var exitPrice = await _derivClient.GetSpotPriceAsync(signal.Asset, stoppingToken);

        if (exitPrice == null)
        {
            _logger.LogWarning("[DashaExecution] Failed to get exit price for {Asset}, skipping signal", signal.Asset);
            signal.Status = DashaPendingSignalStatus.Error;
            signal.EvaluatedAt = DateTime.UtcNow;
            await _dashaRepository.UpdatePendingSignalAsync(signal);
            return;
        }

        signal.ExitPrice = exitPrice.Value;
        signal.EvaluatedAt = DateTime.UtcNow;

        // Step 3: Determine if provider lost
        var providerLost = signal.DidProviderLose();
        signal.ProviderResult = providerLost ? "Lost" : "Won";

        _logger.LogInformation(
            "[DashaExecution] Signal {SignalId}: Entry={Entry}, Exit={Exit}, Direction={Direction}, Provider {Result}",
            signal.PendingSignalId, signal.EntryPrice, signal.ExitPrice, signal.Direction,
            providerLost ? "LOST" : "WON");

        if (!providerLost)
        {
            // Provider won - do NOT execute, just update status
            signal.Status = DashaPendingSignalStatus.ProviderWon;
            await _dashaRepository.UpdatePendingSignalAsync(signal);

            _logger.LogInformation(
                "[DashaExecution] Provider WON on signal {SignalId}. Ignoring (no trade).",
                signal.PendingSignalId);

            return;
        }

        // Step 4: Provider lost - EXECUTE on Deriv
        signal.Status = DashaPendingSignalStatus.ProviderLost;
        await _dashaRepository.UpdatePendingSignalAsync(signal);

        await ExecuteOnDerivAsync(signal, stoppingToken);
    }

    /// <summary>
    /// Execute a trade on Deriv after provider loss is confirmed.
    /// </summary>
    private async Task ExecuteOnDerivAsync(DashaPendingSignal signal, CancellationToken stoppingToken)
    {
        // Get current stake from compounding manager
        var (stake, stakeStep) = await _compoundingManager.GetCurrentStakeAsync(signal.ProviderChannelId);

        // Map direction: DOWN -> PUT, UP -> CALL
        var derivDirection = signal.Direction.ToUpperInvariant() == "DOWN" ? "PUT" : "CALL";

        _logger.LogInformation(
            "[DashaExecution] Executing on Deriv: {Asset} {Direction} ${Stake} {Expiry}min (step {Step})",
            signal.Asset, derivDirection, stake, signal.ExpiryMinutes, stakeStep);

        // Create trade record (before execution for audit trail)
        var trade = new DashaTrade
        {
            PendingSignalId = signal.PendingSignalId,
            ProviderChannelId = signal.ProviderChannelId,
            ProviderName = signal.ProviderName,
            Asset = signal.Asset,
            Direction = derivDirection,
            ExpiryMinutes = signal.ExpiryMinutes,
            Stake = stake,
            StakeStep = stakeStep,
            ProviderEntryPrice = signal.EntryPrice,
            ProviderExitPrice = signal.ExitPrice!.Value,
            ProviderResult = "Lost",
            ProviderSignalAt = signal.SignalReceivedAt,
            ProviderExpiryAt = signal.ExpiryAt,
            CreatedAt = DateTime.UtcNow
        };

        var tradeId = await _dashaRepository.CreateTradeAsync(trade);
        trade.TradeId = tradeId;

        try
        {
            // Execute on Deriv
            var result = await _derivClient.PlaceBinaryOptionAsync(
                signal.Asset,
                derivDirection,
                stake,
                signal.ExpiryMinutes,
                stoppingToken);

            if (result.Success)
            {
                trade.DerivContractId = result.ContractId;
                trade.PurchasePrice = result.PurchasePrice;
                trade.Payout = result.Payout;
                trade.ExecutedAt = DateTime.UtcNow;

                await _dashaRepository.UpdateTradeAsync(trade);

                // Update pending signal status
                signal.Status = DashaPendingSignalStatus.Executed;
                await _dashaRepository.UpdatePendingSignalAsync(signal);

                _logger.LogInformation(
                    "[DashaExecution] Trade executed successfully: ContractId={ContractId}, Stake=${Stake}, Payout=${Payout}",
                    result.ContractId, stake, result.Payout);

                // Send notification
                var stateSummary = await _compoundingManager.GetStateSummaryAsync(signal.ProviderChannelId);
                await SendTradeOpenNotificationAsync(trade, stateSummary);
            }
            else
            {
                _logger.LogError(
                    "[DashaExecution] Trade execution FAILED: {Error} (Code: {Code})",
                    result.ErrorMessage, result.ErrorCode);

                // Don't update compounding state on execution failure
                signal.Status = DashaPendingSignalStatus.Error;
                await _dashaRepository.UpdatePendingSignalAsync(signal);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DashaExecution] Exception during Deriv execution");
            signal.Status = DashaPendingSignalStatus.Error;
            await _dashaRepository.UpdatePendingSignalAsync(signal);
        }
    }

    /// <summary>
    /// Process unsettled trades to check outcomes and update compounding state.
    /// </summary>
    private async Task ProcessUnsettledTradesAsync(CancellationToken stoppingToken)
    {
        var unsettledTrades = await _dashaRepository.GetUnsettledTradesAsync();

        if (unsettledTrades.Count == 0)
            return;

        _logger.LogDebug("[DashaExecution] Checking {Count} unsettled trades", unsettledTrades.Count);

        foreach (var trade in unsettledTrades)
        {
            if (stoppingToken.IsCancellationRequested) break;

            // Skip if not yet expired (contract still open)
            var expectedSettleTime = trade.ExecutedAt?.AddMinutes(trade.ExpiryMinutes) ?? DateTime.MaxValue;
            if (DateTime.UtcNow < expectedSettleTime.AddSeconds(30)) // Add 30s buffer
                continue;

            try
            {
                await CheckTradeOutcomeAsync(trade, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DashaExecution] Error checking outcome for trade {TradeId}", trade.TradeId);
            }
        }
    }

    /// <summary>
    /// Check outcome of a trade and update compounding state.
    /// </summary>
    private async Task CheckTradeOutcomeAsync(DashaTrade trade, CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(trade.DerivContractId))
        {
            _logger.LogWarning("[DashaExecution] Trade {TradeId} has no contract ID", trade.TradeId);
            return;
        }

        if (!_derivClient.IsAuthorized)
        {
            await _derivClient.ConnectAsync(stoppingToken);
            await _derivClient.AuthorizeAsync(stoppingToken);
        }

        var outcome = await _derivClient.GetContractOutcomeAsync(trade.DerivContractId, stoppingToken);

        // Check if contract is still open
        if (outcome.Status == "open" || outcome.Status == "pending")
        {
            _logger.LogDebug("[DashaExecution] Trade {TradeId} still open", trade.TradeId);
            return;
        }

        var won = outcome.Status.ToLowerInvariant() == "win";

        trade.ExecutionResult = won ? "Won" : "Lost";
        trade.Profit = outcome.Profit;
        trade.SettledAt = DateTime.UtcNow;

        await _dashaRepository.UpdateTradeAsync(trade);

        // Update compounding state
        var newStake = await _compoundingManager.RecordTradeResultAsync(
            trade.ProviderChannelId,
            won,
            outcome.Profit);

        _logger.LogInformation(
            "[DashaExecution] Trade {TradeId} SETTLED: {Result}, Profit=${Profit:F2}, New stake=${NewStake}",
            trade.TradeId, trade.ExecutionResult, trade.Profit, newStake);

        // Send notification
        var stateSummary = await _compoundingManager.GetStateSummaryAsync(trade.ProviderChannelId);
        await SendTradeCloseNotificationAsync(trade, stateSummary);
    }

    /// <summary>
    /// Send Telegram notification when trade opens.
    /// </summary>
    private async Task SendTradeOpenNotificationAsync(DashaTrade trade, string stateSummary)
    {
        try
        {
            var message = $"[DashaTrade] TRADE OPENED\n" +
                         $"{trade.Asset} {trade.Direction}\n" +
                         $"Stake: ${trade.Stake:F2} (Step {trade.StakeStep})\n" +
                         $"Provider Entry: {trade.ProviderEntryPrice}\n" +
                         $"Provider Exit: {trade.ProviderExitPrice}\n" +
                         $"(Provider lost, we execute)\n\n" +
                         $"State: {stateSummary}";

            await _notifier.SendTradeMessageAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DashaExecution] Failed to send open notification");
        }
    }

    /// <summary>
    /// Send Telegram notification when trade closes.
    /// </summary>
    private async Task SendTradeCloseNotificationAsync(DashaTrade trade, string stateSummary)
    {
        try
        {
            var emoji = trade.Won ? "+" : "-";
            var message = $"[DashaTrade] TRADE CLOSED\n" +
                         $"{trade.Asset} {trade.Direction}: {trade.ExecutionResult}\n" +
                         $"Profit: {emoji}${Math.Abs(trade.Profit ?? 0):F2}\n\n" +
                         $"State: {stateSummary}";

            await _notifier.SendTradeMessageAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DashaExecution] Failed to send close notification");
        }
    }
}

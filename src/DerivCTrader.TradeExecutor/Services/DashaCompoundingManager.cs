using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// Manages the positive compounding state machine for Dasha Trade.
///
/// Ladder: $50 -> $100 -> $200 -> RESET
/// Rules:
/// - Win at step 0: advance to step 1 ($100)
/// - Win at step 1: advance to step 2 ($200)
/// - Win at step 2: RESET to step 0 ($50) - completed full ladder
/// - Loss at ANY step: RESET to step 0 ($50)
///
/// State is GLOBAL across all signals for a given provider.
/// State persists in database (survives service restarts).
/// </summary>
public class DashaCompoundingManager
{
    private readonly IDashaTradeRepository _repository;
    private readonly ILogger<DashaCompoundingManager> _logger;

    // Default ladder - can be overridden per provider via DashaProviderConfig.LadderSteps
    private readonly decimal[] _defaultLadder = { 50m, 100m, 200m };

    public DashaCompoundingManager(
        IDashaTradeRepository repository,
        ILogger<DashaCompoundingManager> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current stake for a provider.
    /// Creates initial state if not exists.
    /// </summary>
    public async Task<(decimal stake, int step)> GetCurrentStakeAsync(string providerChannelId)
    {
        var state = await GetOrCreateStateAsync(providerChannelId);
        return (state.CurrentStake, state.CurrentStep);
    }

    /// <summary>
    /// Records a trade result and updates the compounding state.
    /// </summary>
    /// <param name="providerChannelId">The provider channel ID</param>
    /// <param name="won">Whether our trade won</param>
    /// <param name="profit">The profit/loss amount</param>
    /// <returns>The new stake for the next trade</returns>
    public async Task<decimal> RecordTradeResultAsync(string providerChannelId, bool won, decimal profit)
    {
        var state = await GetOrCreateStateAsync(providerChannelId);
        var ladder = await GetLadderAsync(providerChannelId);
        var maxStep = ladder.Length - 1;

        var previousStep = state.CurrentStep;
        var previousStake = state.CurrentStake;

        // Update statistics
        state.TotalProfit += profit;
        state.LastTradeAt = DateTime.UtcNow;

        if (won)
        {
            state.ConsecutiveWins++;
            state.TotalWins++;

            if (state.CurrentStep < maxStep)
            {
                // Advance to next step on the ladder
                state.CurrentStep++;
                state.CurrentStake = ladder[state.CurrentStep];

                _logger.LogInformation(
                    "[DashaCompounding] WIN at step {PreviousStep}. Advancing to step {NewStep} (${NewStake}). " +
                    "Consecutive wins: {ConsecutiveWins}. Total profit: ${TotalProfit:F2}",
                    previousStep, state.CurrentStep, state.CurrentStake, state.ConsecutiveWins, state.TotalProfit);
            }
            else
            {
                // Already at max step ($200), RESET regardless of outcome
                state.CurrentStep = 0;
                state.CurrentStake = ladder[0];
                state.ConsecutiveWins = 0;

                _logger.LogInformation(
                    "[DashaCompounding] WIN at MAX step {MaxStep}! Completed full ladder. " +
                    "RESET to step 0 (${InitialStake}). Total profit: ${TotalProfit:F2}",
                    maxStep, ladder[0], state.TotalProfit);
            }
        }
        else
        {
            // ANY loss resets to step 0
            state.TotalLosses++;
            state.CurrentStep = 0;
            state.CurrentStake = ladder[0];
            state.ConsecutiveWins = 0;

            _logger.LogInformation(
                "[DashaCompounding] LOSS at step {PreviousStep} (${PreviousStake}). " +
                "RESET to step 0 (${InitialStake}). Total profit: ${TotalProfit:F2}",
                previousStep, previousStake, ladder[0], state.TotalProfit);
        }

        await _repository.UpdateCompoundingStateAsync(state);

        return state.CurrentStake;
    }

    /// <summary>
    /// Manually resets the state to initial (step 0).
    /// Useful for error recovery or manual override.
    /// </summary>
    public async Task ResetStateAsync(string providerChannelId)
    {
        var state = await GetOrCreateStateAsync(providerChannelId);
        var ladder = await GetLadderAsync(providerChannelId);

        state.CurrentStep = 0;
        state.CurrentStake = ladder[0];
        state.ConsecutiveWins = 0;

        await _repository.UpdateCompoundingStateAsync(state);

        _logger.LogInformation(
            "[DashaCompounding] Manual RESET for {Provider}. Now at step 0 (${Stake})",
            providerChannelId, state.CurrentStake);
    }

    /// <summary>
    /// Gets the compounding state summary for logging/notification.
    /// </summary>
    public async Task<string> GetStateSummaryAsync(string providerChannelId)
    {
        var state = await GetOrCreateStateAsync(providerChannelId);
        var ladder = await GetLadderAsync(providerChannelId);

        return $"Step {state.CurrentStep}/{ladder.Length - 1} | " +
               $"Stake: ${state.CurrentStake:F2} | " +
               $"Wins: {state.TotalWins} | " +
               $"Losses: {state.TotalLosses} | " +
               $"Profit: ${state.TotalProfit:F2}";
    }

    /// <summary>
    /// Gets or creates the compounding state for a provider.
    /// </summary>
    private async Task<DashaCompoundingState> GetOrCreateStateAsync(string providerChannelId)
    {
        var state = await _repository.GetCompoundingStateAsync(providerChannelId);

        if (state == null)
        {
            var ladder = await GetLadderAsync(providerChannelId);

            state = new DashaCompoundingState
            {
                ProviderChannelId = providerChannelId,
                CurrentStep = 0,
                CurrentStake = ladder[0],
                ConsecutiveWins = 0,
                TotalWins = 0,
                TotalLosses = 0,
                TotalProfit = 0
            };

            await _repository.CreateCompoundingStateAsync(state);

            _logger.LogInformation(
                "[DashaCompounding] Created initial state for {Provider} at step 0 (${Stake})",
                providerChannelId, state.CurrentStake);
        }

        return state;
    }

    /// <summary>
    /// Gets the stake ladder for a provider from config.
    /// </summary>
    private async Task<decimal[]> GetLadderAsync(string providerChannelId)
    {
        var config = await _repository.GetProviderConfigAsync(providerChannelId);

        if (config != null)
        {
            try
            {
                return config.GetLadderArray();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[DashaCompounding] Failed to parse ladder for {Provider}, using default",
                    providerChannelId);
            }
        }

        return _defaultLadder;
    }
}

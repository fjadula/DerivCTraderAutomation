using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// Background service that processes unprocessed signals and executes binary options on Deriv
/// </summary>
public class BinaryExecutionService : BackgroundService
{
    private readonly ILogger<BinaryExecutionService> _logger;
    private readonly ITradeRepository _repository;
    private readonly IDerivClient _derivClient;
    private readonly decimal _defaultStake;
    private readonly int _pollIntervalSeconds;

    public BinaryExecutionService(
        ILogger<BinaryExecutionService> logger,
        ITradeRepository repository,
        IDerivClient derivClient,
        IConfiguration configuration)
    {
        _logger = logger;
        _repository = repository;
        _derivClient = derivClient;
        
        _defaultStake = decimal.Parse(configuration["BinaryOptions:DefaultStake"] ?? "20");
        _pollIntervalSeconds = int.Parse(configuration["BinaryExecutor:PollIntervalSeconds"] ?? "5");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== BINARY EXECUTION SERVICE STARTING ===");
        Console.WriteLine("=== BINARY EXECUTION SERVICE STARTING ===");

        // Connect to Deriv
        try
        {
            await _derivClient.ConnectAsync(stoppingToken);
            await _derivClient.AuthorizeAsync(stoppingToken);
            
            var balance = await _derivClient.GetBalanceAsync(stoppingToken);
            _logger.LogInformation("✅ Deriv connected. Balance: ${Balance}", balance);
            Console.WriteLine($"✅ Deriv connected. Balance: ${balance}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Deriv");
            Console.WriteLine($"❌ Deriv connection failed: {ex.Message}");
            return;
        }

        // Main processing loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessUnprocessedSignalsAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in binary execution loop");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        await _derivClient.DisconnectAsync();
        _logger.LogInformation("=== BINARY EXECUTION SERVICE STOPPED ===");
    }

    private async Task ProcessUnprocessedSignalsAsync(CancellationToken cancellationToken)
    {
        var signals = await _repository.GetUnprocessedSignalsAsync();

        if (signals.Count == 0)
            return;

        _logger.LogInformation("📋 Found {Count} unprocessed signals", signals.Count);
        Console.WriteLine($"📋 Found {signals.Count} unprocessed signals");

        foreach (var signal in signals)
        {
            try
            {
                await ProcessSignalAsync(signal, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process signal {SignalId}", signal.SignalId);
            }
        }
    }

    private async Task ProcessSignalAsync(ParsedSignal signal, CancellationToken cancellationToken)
    {
        _logger.LogInformation("🔨 Processing signal #{SignalId}: {Asset} {Direction}", 
            signal.SignalId, signal.Asset, signal.Direction);
        Console.WriteLine($"🔨 Processing: {signal.Asset} {signal.Direction}");

        // Calculate expiry
        int expiryMinutes = CalculateExpiry(signal);

        // Map direction
        string direction = signal.Direction.ToString().ToUpper();
        if (direction == "BUY") direction = "CALL";
        if (direction == "SELL") direction = "PUT";

        // Execute binary option
        var result = await _derivClient.PlaceBinaryOptionAsync(
            signal.Asset,
            direction,
            _defaultStake,
            expiryMinutes,
            cancellationToken);

        if (!result.Success || string.IsNullOrEmpty(result.ContractId))
        {
            _logger.LogError("Failed to execute binary: {Error}", result.ErrorMessage);
            return;
        }

        // Save to TradeExecutionQueue
        var queueItem = new TradeExecutionQueue
        {
            Asset = signal.Asset,
            Direction = direction,
            Platform = "Deriv",
            DerivContractId = result.ContractId,
            Stake = _defaultStake,
            ExpiryMinutes = expiryMinutes,
            StrategyName = $"{signal.Asset}_{DateTime.UtcNow:yyyyMMddHHmm}_{direction}",
            CreatedAt = DateTime.UtcNow,
            Timeframe = signal.Timeframe,
            Pattern = signal.Pattern,
            ProviderChannelId = signal.ProviderChannelId,
            ProviderName = signal.ProviderName
        };

        await _repository.EnqueueTradeAsync(queueItem);

        // Mark signal as processed
        await _repository.MarkSignalAsProcessedAsync(signal.SignalId);

        _logger.LogInformation("✅ TRADE EXECUTED: {Asset} {Direction} ${Stake} {Expiry}min - Contract: {ContractId}",
            signal.Asset, direction, _defaultStake, expiryMinutes, result.ContractId);
        Console.WriteLine($"✅ EXECUTED: {signal.Asset} {direction} ${_defaultStake} {expiryMinutes}min");
        Console.WriteLine($"   Contract: {result.ContractId}");

        // Log balance
        try
        {
            var balance = await _derivClient.GetBalanceAsync(cancellationToken);
            Console.WriteLine($"💰 Balance: ${balance}");
        }
        catch { }
    }

    private int CalculateExpiry(ParsedSignal signal)
    {
        // Volatility indices: 15 minutes
        if (signal.Asset.Contains("VIX", StringComparison.OrdinalIgnoreCase) ||
            signal.Asset.Contains("Volatility", StringComparison.OrdinalIgnoreCase))
        {
            return 15;
        }

        // Forex/Commodities: 21 minutes (Deriv minimum)
        return 21;
    }
}
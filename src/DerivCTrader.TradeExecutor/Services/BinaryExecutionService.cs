using System.Globalization;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Infrastructure.Deriv;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
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
        
        _defaultStake = decimal.Parse(
            configuration["BinaryOptions:DefaultStake"] ?? "20",
            CultureInfo.InvariantCulture);
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
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in binary execution loop");
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
        _logger.LogInformation("=== BINARY EXECUTION SERVICE STOPPED ===");
    }

    private async Task ProcessUnprocessedSignalsAsync(CancellationToken cancellationToken)
    {
        var signals = await _repository.GetUnprocessedSignalsAsync();

        if (signals.Count == 0)
            return;

        // Filter for PURE BINARY signals only (forex signals handled by CTraderForexProcessorService)
        var pureBinarySignals = signals
            .Where(s => s.SignalType == SignalType.PureBinary)
            .ToList();

        if (pureBinarySignals.Count == 0)
            return;

        _logger.LogInformation("📋 Found {Count} unprocessed pure binary signals", pureBinarySignals.Count);
        Console.WriteLine($"📋 Found {pureBinarySignals.Count} pure binary signal(s)");

        foreach (var signal in pureBinarySignals)
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

        // ⚠️ BOOM/CRASH EXCLUSION: These indices do NOT support binary options on Deriv
        if (signal.Asset.Contains("Boom", StringComparison.OrdinalIgnoreCase) ||
            signal.Asset.Contains("Crash", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("⚠️ Skipping binary execution for {Asset} - Boom/Crash indices do not support binary options", 
                signal.Asset);
            Console.WriteLine($"⚠️ SKIPPED: {signal.Asset} - No binary support for Boom/Crash");
            
            // Mark signal as processed to prevent retry
            await _repository.MarkSignalAsProcessedAsync(signal.SignalId);
            
            _logger.LogInformation("✅ Signal #{SignalId} marked as processed (Boom/Crash exclusion)", signal.SignalId);
            return;
        }

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

        // Write to TradeExecutionQueue for KhulaFxTradeMonitor to pick up strategy name
        // Generate a unique order ID for pure binary signals (not from cTrader)
        string generatedOrderId = $"PB_{DateTime.UtcNow:yyyyMMddHHmmss}_{signal.SignalId}";
        string strategyName = string.IsNullOrEmpty(signal.ProviderName) 
            ? $"PureBinary_{signal.Asset}" 
            : signal.ProviderName;

        var queueEntry = new TradeExecutionQueue
        {
            CTraderOrderId = generatedOrderId,
            Asset = signal.Asset,
            Direction = direction,
            StrategyName = strategyName,
            IsOpposite = false,
            ProviderChannelId = signal.ProviderChannelId,
            DerivContractId = result.ContractId,  // Save Deriv contract ID for reference
            CreatedAt = DateTime.UtcNow
        };

        await _repository.EnqueueTradeAsync(queueEntry);

        // Mark signal as processed IMMEDIATELY after successful execution
        await _repository.MarkSignalAsProcessedAsync(signal.SignalId);

        _logger.LogInformation("✅ TRADE EXECUTED: {Asset} {Direction} ${Stake} {Expiry}min - Contract: {ContractId}",
            signal.Asset, direction, _defaultStake, expiryMinutes, result.ContractId);
        _logger.LogInformation("📝 Queue entry created: OrderId={OrderId}, Strategy={Strategy}, ContractId={ContractId}", 
            generatedOrderId, strategyName, result.ContractId);
        Console.WriteLine($"✅ EXECUTED: {signal.Asset} {direction} ${_defaultStake} {expiryMinutes}min");
        Console.WriteLine($"   Contract: {result.ContractId}");
        Console.WriteLine($"   Strategy: {strategyName}");

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
        // Check if expiry is specified in Timeframe field.
        // Accept formats:
        // - "5M" / "10m"
        // - "5" (when DB column is INT and Dapper maps it back to string)
        // - "5 Min" / "5 Minutes"
        // Used by DerivPlus and other providers that specify expiry per signal
        if (!string.IsNullOrWhiteSpace(signal.Timeframe))
        {
            var tf = signal.Timeframe.Trim();

            // 1) Pure number => minutes
            if (int.TryParse(tf, out var numericMinutes) && numericMinutes > 0)
            {
                _logger.LogInformation("Using signal-specified expiry from Timeframe (numeric): {Minutes} minutes", numericMinutes);
                return numericMinutes;
            }

            // 2) Common minute formats
            var timeframeMatch = System.Text.RegularExpressions.Regex.Match(
                tf,
                @"^(\d+)\s*(M|MIN|MINS|MINUTE|MINUTES)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (timeframeMatch.Success && int.TryParse(timeframeMatch.Groups[1].Value, out var tfExpiry) && tfExpiry > 0)
            {
                _logger.LogInformation("Using signal-specified expiry from Timeframe: {Minutes} minutes", tfExpiry);
                return tfExpiry;
            }
        }

        // Provider-specific expiry settings
        var providerExpiry = GetProviderExpiry(signal.ProviderName);
        if (providerExpiry.HasValue)
        {
            _logger.LogInformation("Using provider-specific expiry for {Provider}: {Minutes} minutes",
                signal.ProviderName, providerExpiry.Value);
            return providerExpiry.Value;
        }

        // Asset-based defaults
        if (signal.Asset.Contains("VIX", StringComparison.OrdinalIgnoreCase) ||
            signal.Asset.Contains("Volatility", StringComparison.OrdinalIgnoreCase))
        {
            return 30;
        }

        // Default: 30 minutes
        return 30;
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
            _ => null
        };
    }
}
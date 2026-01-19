using DerivCTrader.Application.Interfaces;
using DerivCTrader.Infrastructure.CTrader.Extensions;
using DerivCTrader.Infrastructure.Persistence;
using DerivCTrader.Infrastructure.Deriv;
using DerivCTrader.Infrastructure.ExpiryCalculation;
using DerivCTrader.Infrastructure.Notifications;
using DerivCTrader.Infrastructure.MarketData;
using DerivCTrader.Infrastructure.Execution;
using DerivCTrader.TradeExecutor.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor;

class Program
{
    static async Task Main(string[] args)
    {
        // CRITICAL FIX: Parse --contentRoot from args FIRST
        var contentRoot = Directory.GetCurrentDirectory();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--contentRoot" || args[i] == "--contentroot")
            {
                contentRoot = args[i + 1];
                Directory.SetCurrentDirectory(contentRoot);
                break;
            }
        }

        Console.WriteLine("========================================");
        Console.WriteLine("  DERIVCTRADER TRADE EXECUTOR");
        Console.WriteLine("========================================");
        Console.WriteLine($"📁 Working Directory: {contentRoot}");
        Console.WriteLine();

        // Load configuration
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(contentRoot); // Use contentRoot instead of GetCurrentDirectory()

        var productionFile = Path.Combine(contentRoot, "appsettings.Production.json");
        if (File.Exists(productionFile))
        {
            configBuilder.AddJsonFile("appsettings.Production.json", optional: false, reloadOnChange: true);
            Console.WriteLine("✅ Using appsettings.Production.json");
        }
        else
        {
            configBuilder.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            Console.WriteLine("✅ Using appsettings.json");
        }

        var configuration = configBuilder.Build();

        // One-off maintenance mode: mark a parsed signal as unprocessed and exit.
        // Usage: dotnet run -- --reprocess-signal 41
        var reprocessSignalId = TryGetIntArg(args, "--reprocess-signal");
        if (reprocessSignalId.HasValue)
        {
            Console.WriteLine($"🔁 Reprocessing requested for SignalId={reprocessSignalId.Value}...");

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<ITradeRepository, SqlServerTradeRepository>();

            await using var provider = services.BuildServiceProvider();
            var repo = provider.GetRequiredService<ITradeRepository>();
            await repo.MarkSignalAsUnprocessedAsync(reprocessSignalId.Value);

            Console.WriteLine($"✅ SignalId={reprocessSignalId.Value} marked as unprocessed. Start TradeExecutor normally to process it.");
            return;
        }

        // Check for backtest mode
        var mode = TryGetStringArg(args, "--mode");
        if (mode?.Equals("backtest", StringComparison.OrdinalIgnoreCase) == true)
        {
            await RunBacktestModeAsync(args, configuration, contentRoot);
            return;
        }

        // Check for CMFLIX backtest mode
        if (mode?.Equals("cmflix-backtest", StringComparison.OrdinalIgnoreCase) == true)
        {
            await RunCmflixBacktestAsync(args, configuration, contentRoot);
            return;
        }

        // Check for CMFLIX martingale backtest mode (enter after provider loses)
        if (mode?.Equals("cmflix-martingale", StringComparison.OrdinalIgnoreCase) == true)
        {
            await RunCmflixMartingaleBacktestAsync(args, configuration, contentRoot);
            return;
        }

        // Check for CMFLIX multi-day backtest
        if (mode?.Equals("cmflix-multi", StringComparison.OrdinalIgnoreCase) == true)
        {
            await RunCmflixMultiDayBacktestAsync(args, configuration, contentRoot);
            return;
        }

        // Check for import mode
        var importPath = TryGetStringArg(args, "--import");
        if (!string.IsNullOrEmpty(importPath))
        {
            await RunImportModeAsync(args, configuration, importPath);
            return;
        }

        // Check for fetch-data mode (FinanceFlowAPI)
        if (HasFlag(args, "--fetch-data"))
        {
            await RunFetchDataModeAsync(args, configuration, contentRoot);
            return;
        }

        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .WriteTo.File(
                Path.Combine(contentRoot, "logs", "tradeexecutor-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            .CreateLogger();

        try
        {
            Log.Information("Starting Deriv cTrader Trade Executor...");
            Log.Information("Working directory: {ContentRoot}", contentRoot);

            var host = Host.CreateDefaultBuilder(args)
                .UseContentRoot(contentRoot) // Set content root explicitly
                .UseSerilog()
                .ConfigureServices((context, services) =>
                {
                    // Configuration
                    services.AddSingleton<IConfiguration>(configuration);

                    // Deriv configuration
                    var derivConfig = new DerivConfig();
                    configuration.GetSection("Deriv").Bind(derivConfig);
                    services.AddSingleton(derivConfig);

                    // Database
                    services.AddSingleton<ITradeRepository, SqlServerTradeRepository>();
                    services.AddSingleton<IStrategyRepository, SqlServerStrategyRepository>();

                    // Telegram notifications (trade fill/close)
                    services.AddSingleton<ITelegramNotifier, TelegramNotifier>();

                    // Yahoo Finance HTTP client for DowOpenGS
                    services.AddHttpClient<IYahooFinanceService, YahooFinanceService>();

                    // Polygon/MassiveAPI HTTP client for DowOpenGS
                    services.AddHttpClient<IMassiveApiService, MassiveApiService>();

                    // Market executor for DowOpenGS (Deriv CFD + Binary)
                    services.AddSingleton<IMarketExecutor, DerivMarketExecutor>();

                    // Deriv client - TRANSIENT so each service gets its own WebSocket connection
                    services.AddTransient<IDerivClient, DerivClient>();

                    // Binary expiry calculator
                    services.AddSingleton<IBinaryExpiryCalculator, BinaryExpiryCalculator>();

                    // cTrader services

                    services.AddCTraderServices(configuration);

                    // Register SQL Server push notification service (singleton)
                    services.AddSingleton<SignalSqlNotificationService>(sp =>
                    {
                        var config = sp.GetRequiredService<IConfiguration>();
                        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                        var logger = loggerFactory.CreateLogger<SignalSqlNotificationService>();
                        var connStr = config.GetConnectionString("ConnectionString")!;
                        var svc = new SignalSqlNotificationService(connStr, logger);
                        svc.StartListening();
                        return svc;
                    });

                    // Register background services (ORDER MATTERS - Symbol initializer must run first!)
                    services.AddHostedService<CTraderSymbolInitializerService>(); // FIRST: Initialize cTrader & symbols
                    services.AddHostedService<CTraderForexProcessorService>();    // Process forex signals -> cTrader
                    services.AddHostedService<ChartSenseSetupService>();          // Process ChartSense image signals
                    services.AddHostedService<ChartSenseTimeoutMonitor>();        // Monitor ChartSense setup timeouts

                    // Register DerivBinaryExecutorService as singleton and hosted service for event-driven injection
                    services.AddSingleton<DerivBinaryExecutorService>();
                    services.AddHostedService(sp => sp.GetRequiredService<DerivBinaryExecutorService>());

                    services.AddHostedService<BinaryExecutionService>();          // Process pure binary signals -> Deriv
                    services.AddHostedService<OutcomeMonitorService>();

                    // TODO: Wire up SignalSqlNotificationService.SignalChanged to trigger processing in background services

                    // Bridge: Hosted service to wire up SQL notification to DerivBinaryExecutorService
                    services.AddHostedService<SqlToDerivExecutorBridgeService>();

                    // DowOpenGS Strategy Services
                    services.AddHostedService<PreviousCloseService>();            // Cache closes at 21:00 UTC
                    services.AddHostedService<DowOpenGSService>();                // Execute at 14:30 UTC

                    // Dasha Trade Selective Martingale Services
                    services.AddSingleton<IDashaTradeRepository, SqlServerDashaTradeRepository>();
                    services.AddSingleton<DashaCompoundingManager>();
                    services.AddHostedService<DashaTradeExecutionService>();      // Selective martingale execution

                    // Scheduled Binary Execution (CMFLIX and similar providers)
                    services.AddHostedService<ScheduledBinaryExecutionService>(); // Time-based binary execution

                    Log.Information("Services registered successfully");
                })
                .Build();
            Console.WriteLine("\n========================================");
            Console.WriteLine("  SERVICES REGISTERED");
            Console.WriteLine("========================================");
            Console.WriteLine("✅ Database Repository");
            Console.WriteLine("✅ cTrader Services (Client, Orders, Monitor)");
            Console.WriteLine("✅ Deriv WebSocket Client");
            Console.WriteLine("✅ cTrader Symbol Initializer (runs first)");
            Console.WriteLine("✅ cTrader Forex Processor Service");
            Console.WriteLine("✅ ChartSense Setup Service (Image Signals)");
            Console.WriteLine("✅ ChartSense Timeout Monitor");
            Console.WriteLine("✅ Deriv Binary Executor Service (Queue)");
            Console.WriteLine("✅ Binary Execution Service (Pure Binary)");
            Console.WriteLine("✅ Outcome Monitor Service");
            Console.WriteLine("✅ Previous Close Cache Service (21:00 UTC)");
            Console.WriteLine("✅ DowOpenGS Strategy Service (14:30 UTC)");
            Console.WriteLine("✅ Dasha Trade Selective Martingale Service");
            Console.WriteLine("✅ Scheduled Binary Execution Service (CMFLIX)");
            Console.WriteLine("========================================\n");

            Log.Information("Host built successfully, starting services...");
            await host.RunAsync();

            Console.WriteLine("\n=== HOST STOPPED ===");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n=== FATAL ERROR: {ex.Message} ===");
            Console.WriteLine(ex.StackTrace);
            Log.Fatal(ex, "Application terminated unexpectedly");

            // Don't wait for key press when running as service
            if (Environment.UserInteractive)
            {
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
            }
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static int? TryGetIntArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(args[i + 1], out var value))
                    return value;
            }
        }

        return null;
    }

    private static string? TryGetStringArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static DateTime? TryGetDateArg(string[] args, string name)
    {
        var value = TryGetStringArg(args, name);
        if (DateTime.TryParse(value, out var date))
            return date;
        return null;
    }

    private static bool HasFlag(string[] args, string name)
    {
        return args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Run backtest mode.
    /// Usage: dotnet run -- --mode=backtest --from=2024-01-01 --to=2024-06-30
    /// </summary>
    private static async Task RunBacktestModeAsync(string[] args, IConfiguration configuration, string contentRoot)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("  DOWOPENGS BACKTEST MODE");
        Console.WriteLine("========================================\n");

        var fromDate = TryGetDateArg(args, "--from");
        var toDate = TryGetDateArg(args, "--to");

        if (!fromDate.HasValue || !toDate.HasValue)
        {
            Console.WriteLine("ERROR: --from and --to dates are required");
            Console.WriteLine("\nUsage:");
            Console.WriteLine("  dotnet run -- --mode=backtest --from=2024-01-01 --to=2024-06-30");
            Console.WriteLine("\nExample:");
            Console.WriteLine("  dotnet run -- --mode=backtest --from=2024-01-01 --to=2024-03-31");
            return;
        }

        // Configure logging
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(contentRoot, "logs", "backtest-.txt"),
                rollingInterval: RollingInterval.Day)
            .CreateLogger();

        // Build services for backtest
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog());
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IStrategyRepository, SqlServerStrategyRepository>();
        services.AddSingleton<IBacktestRepository, SqlServerBacktestRepository>();
        services.AddSingleton<DowOpenGSBacktestRunner>();

        await using var provider = services.BuildServiceProvider();

        // Check data coverage first
        var backtestRepo = provider.GetRequiredService<IBacktestRepository>();

        Console.WriteLine("Checking data coverage...\n");
        var gsData = await backtestRepo.GetDataCoverageAsync("GS");
        var ymData = await backtestRepo.GetDataCoverageAsync("YM=F");
        var ws30Data = await backtestRepo.GetDataCoverageAsync("WS30");

        Console.WriteLine($"  GS: {(gsData.Count > 0 ? $"{gsData.Earliest:yyyy-MM-dd} to {gsData.Latest:yyyy-MM-dd} ({gsData.Count:N0} candles)" : "NO DATA")}");
        Console.WriteLine($"  YM=F: {(ymData.Count > 0 ? $"{ymData.Earliest:yyyy-MM-dd} to {ymData.Latest:yyyy-MM-dd} ({ymData.Count:N0} candles)" : "NO DATA")}");
        Console.WriteLine($"  WS30: {(ws30Data.Count > 0 ? $"{ws30Data.Earliest:yyyy-MM-dd} to {ws30Data.Latest:yyyy-MM-dd} ({ws30Data.Count:N0} candles)" : "NO DATA")}");

        if (gsData.Count == 0 || ymData.Count == 0 || ws30Data.Count == 0)
        {
            Console.WriteLine("\nWARNING: Missing historical data. Import data first:");
            Console.WriteLine("  dotnet run -- --import=C:\\path\\to\\csvs --intraday");
            Console.WriteLine("\nRequired CSV files: GS.csv, YM_F.csv, WS30.csv");
        }

        Console.WriteLine();

        // Run backtest
        var runner = provider.GetRequiredService<DowOpenGSBacktestRunner>();
        var notes = TryGetStringArg(args, "--notes");

        try
        {
            var result = await runner.RunBacktestAsync(fromDate.Value, toDate.Value, notes);
            Console.WriteLine($"\nBacktest completed. Run ID: {result.RunId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nBacktest FAILED: {ex.Message}");
            Log.Error(ex, "Backtest failed");
        }

        Log.CloseAndFlush();
    }

    /// <summary>
    /// Run CMFLIX backtest mode using real Deriv price data.
    /// Usage: dotnet run -- --mode=cmflix-backtest --signal="CMFLIX GOLD SIGNALS..."
    /// </summary>
    private static async Task RunCmflixBacktestAsync(string[] args, IConfiguration configuration, string contentRoot)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("  CMFLIX BACKTEST MODE (REAL DERIV DATA)");
        Console.WriteLine("========================================\n");

        // Configure logging
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(contentRoot, "logs", "cmflix-backtest-.txt"),
                rollingInterval: RollingInterval.Day)
            .CreateLogger();

        // Hardcoded today's signals for testing
        var signalMessage = @"CMFLIX GOLD SIGNALS
09/01
5 MINUTOS

* 09:55 - EUR/USD- CALL
* 10:05 - GBP/USD - CALL
* 10:15 - EUR/GBP - CALL
* 10:35 - USDCAD - CALL
* 10:50 - EUR/USD - CALL
* 10:55 - EUR/USD - CALL
* 11:15 - EUR/USD - CALL
* 11:25 - AUD/JPY- CALL
* 11:45 - AUD/CAD- PUT
* 12:00 - USD/CAD - CALL
* 12:15 - EUR/USD - PUT
* 12:25 - EUR/USD - CALL
* 12:45 - EUR/USD - PUT
* 12:55 - EUR/USD - PUT
* 13:25 - AUD/JPY - CALL
* 13:30 - GBP/USD - CALL
* 13:40 - USD/CAD - PUT
* 13:55 - EUR/USD - PUT
* 14:05 - EUR/USD - CALL
* 14:25 - EUR/USD - PUT
* 14:35 - USD/CAD - PUT
* 14:50 - EUR/USD - PUT
* 15:05 - EUR/USD - CALL
* 15:15 - EUR/USD - CALL
* 15:30 - USD/CAD - PUT
* 15:45 - EUR/USD - PUT
* 16:05 - EUR/USD - PUT
* 16:20 - GBP/USD - CALL";

        // Build services
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog());
        services.AddSingleton<IConfiguration>(configuration);

        // Deriv client
        var derivConfig = new DerivConfig();
        configuration.GetSection("Deriv").Bind(derivConfig);
        services.AddSingleton(derivConfig);
        services.AddSingleton<IDerivClient, DerivClient>();

        // Parser
        services.AddSingleton<DerivCTrader.Application.Parsers.CmflixParser>();

        await using var provider = services.BuildServiceProvider();

        var derivClient = provider.GetRequiredService<IDerivClient>();
        var parser = provider.GetRequiredService<DerivCTrader.Application.Parsers.CmflixParser>();

        try
        {
            // Connect to Deriv
            Console.WriteLine("Connecting to Deriv...");
            await derivClient.ConnectAsync();
            Console.WriteLine("Connected!\n");

            // Parse signals
            var testDate = new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc);
            var signals = parser.ParseBatch(signalMessage, overrideDate: testDate);
            Console.WriteLine($"Parsed {signals.Count} signals\n");

            var stakeUsd = configuration.GetValue<decimal>("Cmflix:StakeUsd", 20m);
            var payoutPercent = 0.85m;
            var now = DateTime.UtcNow;

            var results = new List<(string Asset, string Dir, DateTime Time, decimal Entry, decimal Exit, bool Won, decimal PnL)>();

            Console.WriteLine("Fetching prices from Deriv API...\n");
            Console.WriteLine("{0,-4} {1,-8} {2,-6} {3,-8} {4,-12} {5,-12} {6,-8} {7,-10}",
                "#", "Asset", "Dir", "Time", "Entry", "Exit", "Result", "P&L");
            Console.WriteLine(new string('-', 80));

            int signalNum = 0;
            foreach (var signal in signals)
            {
                signalNum++;
                var entryTime = signal.ScheduledAtUtc!.Value;
                var exitTime = entryTime.AddMinutes(15);

                // Skip future signals
                if (exitTime > now)
                {
                    Console.WriteLine("{0,-4} {1,-8} {2,-6} {3,-8} -- FUTURE (not yet expired) --",
                        signalNum, signal.Asset, signal.Direction == DerivCTrader.Domain.Enums.TradeDirection.Call ? "CALL" : "PUT",
                        entryTime.ToString("HH:mm"));
                    continue;
                }

                // Get prices
                var entryPrice = await derivClient.GetHistoricalPriceAsync(signal.Asset, entryTime);
                await Task.Delay(300); // Rate limit
                var exitPrice = await derivClient.GetHistoricalPriceAsync(signal.Asset, exitTime);
                await Task.Delay(300); // Rate limit

                if (entryPrice == null || exitPrice == null)
                {
                    Console.WriteLine("{0,-4} {1,-8} {2,-6} {3,-8} -- NO DATA --",
                        signalNum, signal.Asset, signal.Direction == DerivCTrader.Domain.Enums.TradeDirection.Call ? "CALL" : "PUT",
                        entryTime.ToString("HH:mm"));
                    continue;
                }

                var isCall = signal.Direction == DerivCTrader.Domain.Enums.TradeDirection.Call;
                var won = (isCall && exitPrice > entryPrice) || (!isCall && exitPrice < entryPrice);
                var pnl = won ? stakeUsd * payoutPercent : -stakeUsd;

                results.Add((signal.Asset, isCall ? "CALL" : "PUT", entryTime, entryPrice.Value, exitPrice.Value, won, pnl));

                Console.WriteLine("{0,-4} {1,-8} {2,-6} {3,-8} {4,-12:F5} {5,-12:F5} {6,-8} {7,-10}",
                    signalNum, signal.Asset, isCall ? "CALL" : "PUT", entryTime.ToString("HH:mm"),
                    entryPrice, exitPrice, won ? "WIN" : "LOSS",
                    pnl >= 0 ? $"+${pnl:F2}" : $"-${Math.Abs(pnl):F2}");
            }

            // Summary
            if (results.Count > 0)
            {
                var wins = results.Count(r => r.Won);
                var losses = results.Count - wins;
                var winRate = (decimal)wins / results.Count * 100;
                var totalPnL = results.Sum(r => r.PnL);

                Console.WriteLine("\n" + new string('=', 80));
                Console.WriteLine("SUMMARY");
                Console.WriteLine(new string('=', 80));
                Console.WriteLine($"  Completed Signals: {results.Count} (of {signals.Count} total)");
                Console.WriteLine($"  Wins:              {wins}");
                Console.WriteLine($"  Losses:            {losses}");
                Console.WriteLine($"  Win Rate:          {winRate:F1}%");
                Console.WriteLine($"  Total P&L:         {(totalPnL >= 0 ? "+" : "")}${totalPnL:F2}");
                Console.WriteLine($"  Stake per trade:   ${stakeUsd}");
                Console.WriteLine($"  Payout:            {payoutPercent * 100}%");
                Console.WriteLine();

                // Asset breakdown
                Console.WriteLine("BY ASSET:");
                foreach (var group in results.GroupBy(r => r.Asset).OrderByDescending(g => g.Count()))
                {
                    var assetWins = group.Count(r => r.Won);
                    var assetWinRate = (decimal)assetWins / group.Count() * 100;
                    var assetPnL = group.Sum(r => r.PnL);
                    Console.WriteLine($"  {group.Key,-8}: {group.Count()} trades, {assetWins} wins ({assetWinRate:F0}%), P&L: {(assetPnL >= 0 ? "+" : "")}${assetPnL:F2}");
                }
            }
            else
            {
                Console.WriteLine("\nNo completed signals yet - all signals may be in the future.");
                Console.WriteLine($"Current UTC time: {now:HH:mm:ss}");
                Console.WriteLine($"First signal expires at: {signals.First().ScheduledAtUtc!.Value.AddMinutes(15):HH:mm:ss} UTC");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nBacktest FAILED: {ex.Message}");
            Log.Error(ex, "CMFLIX backtest failed");
        }
        finally
        {
            await derivClient.DisconnectAsync();
        }

        Log.CloseAndFlush();
    }

    /// <summary>
    /// Run CMFLIX martingale backtest - only enter trades after provider loses.
    /// Strategy: If CMFLIX signal loses, enter same direction at expiry (step 1 martingale).
    /// Usage: dotnet run -- --mode cmflix-martingale
    /// </summary>
    private static async Task RunCmflixMartingaleBacktestAsync(string[] args, IConfiguration configuration, string contentRoot)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("  CMFLIX MARTINGALE BACKTEST");
        Console.WriteLine("  (Enter after provider loses)");
        Console.WriteLine("========================================\n");

        // Configure logging
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(contentRoot, "logs", "cmflix-martingale-.txt"),
                rollingInterval: RollingInterval.Day)
            .CreateLogger();

        // Today's signals
        var signalMessage = @"CMFLIX GOLD SIGNALS
09/01
5 MINUTOS

* 09:55 - EUR/USD- CALL
* 10:05 - GBP/USD - CALL
* 10:15 - EUR/GBP - CALL
* 10:35 - USDCAD - CALL
* 10:50 - EUR/USD - CALL
* 10:55 - EUR/USD - CALL
* 11:15 - EUR/USD - CALL
* 11:25 - AUD/JPY- CALL
* 11:45 - AUD/CAD- PUT
* 12:00 - USD/CAD - CALL
* 12:15 - EUR/USD - PUT
* 12:25 - EUR/USD - CALL
* 12:45 - EUR/USD - PUT
* 12:55 - EUR/USD - PUT
* 13:25 - AUD/JPY - CALL
* 13:30 - GBP/USD - CALL
* 13:40 - USD/CAD - PUT
* 13:55 - EUR/USD - PUT
* 14:05 - EUR/USD - CALL
* 14:25 - EUR/USD - PUT
* 14:35 - USD/CAD - PUT
* 14:50 - EUR/USD - PUT
* 15:05 - EUR/USD - CALL
* 15:15 - EUR/USD - CALL
* 15:30 - USD/CAD - PUT
* 15:45 - EUR/USD - PUT
* 16:05 - EUR/USD - PUT
* 16:20 - GBP/USD - CALL";

        // Build services
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog());
        services.AddSingleton<IConfiguration>(configuration);

        var derivConfig = new DerivConfig();
        configuration.GetSection("Deriv").Bind(derivConfig);
        services.AddSingleton(derivConfig);
        services.AddSingleton<IDerivClient, DerivClient>();
        services.AddSingleton<DerivCTrader.Application.Parsers.CmflixParser>();

        await using var provider = services.BuildServiceProvider();

        var derivClient = provider.GetRequiredService<IDerivClient>();
        var parser = provider.GetRequiredService<DerivCTrader.Application.Parsers.CmflixParser>();

        try
        {
            Console.WriteLine("Connecting to Deriv...");
            await derivClient.ConnectAsync();
            Console.WriteLine("Connected!\n");

            var testDate = new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc);
            var signals = parser.ParseBatch(signalMessage, overrideDate: testDate);
            Console.WriteLine($"Parsed {signals.Count} signals\n");

            var stakeUsd = configuration.GetValue<decimal>("Cmflix:StakeUsd", 20m);
            var payoutPercent = 0.85m;
            var now = DateTime.UtcNow;

            Console.WriteLine("STRATEGY: Only enter after CMFLIX loses (same direction)");
            Console.WriteLine($"          Entry at T+15, Exit at T+30 (15 min expiry)");
            Console.WriteLine($"          Stake: ${stakeUsd}, Payout: {payoutPercent * 100}%\n");

            // Phase 1: Evaluate all CMFLIX signals
            Console.WriteLine("PHASE 1: Evaluating CMFLIX signal outcomes...\n");
            Console.WriteLine("{0,-4} {1,-8} {2,-6} {3,-8} {4,-12} {5,-12} {6,-10}",
                "#", "Asset", "Dir", "Time", "Entry", "Exit", "CMFLIX");
            Console.WriteLine(new string('-', 70));

            var cmflixResults = new List<(int Num, string Asset, string Dir, DateTime SignalTime, decimal Entry, decimal Exit, bool CmflixWon)>();

            int signalNum = 0;
            foreach (var signal in signals)
            {
                signalNum++;
                var signalTime = signal.ScheduledAtUtc!.Value;
                var cmflixExitTime = signalTime.AddMinutes(15);

                // Skip if CMFLIX hasn't expired yet
                if (cmflixExitTime > now)
                {
                    Console.WriteLine("{0,-4} {1,-8} {2,-6} {3,-8} -- PENDING --",
                        signalNum, signal.Asset,
                        signal.Direction == DerivCTrader.Domain.Enums.TradeDirection.Call ? "CALL" : "PUT",
                        signalTime.ToString("HH:mm"));
                    continue;
                }

                var entryPrice = await derivClient.GetHistoricalPriceAsync(signal.Asset, signalTime);
                await Task.Delay(200);
                var exitPrice = await derivClient.GetHistoricalPriceAsync(signal.Asset, cmflixExitTime);
                await Task.Delay(200);

                if (entryPrice == null || exitPrice == null)
                {
                    Console.WriteLine("{0,-4} {1,-8} {2,-6} {3,-8} -- NO DATA --",
                        signalNum, signal.Asset,
                        signal.Direction == DerivCTrader.Domain.Enums.TradeDirection.Call ? "CALL" : "PUT",
                        signalTime.ToString("HH:mm"));
                    continue;
                }

                var isCall = signal.Direction == DerivCTrader.Domain.Enums.TradeDirection.Call;
                var cmflixWon = (isCall && exitPrice > entryPrice) || (!isCall && exitPrice < entryPrice);

                cmflixResults.Add((signalNum, signal.Asset, isCall ? "CALL" : "PUT", signalTime, entryPrice.Value, exitPrice.Value, cmflixWon));

                Console.WriteLine("{0,-4} {1,-8} {2,-6} {3,-8} {4,-12:F5} {5,-12:F5} {6,-10}",
                    signalNum, signal.Asset, isCall ? "CALL" : "PUT", signalTime.ToString("HH:mm"),
                    entryPrice, exitPrice, cmflixWon ? "WON" : "LOST");
            }

            // Phase 2: Execute martingale trades on losses
            var losses = cmflixResults.Where(r => !r.CmflixWon).ToList();
            Console.WriteLine($"\n\nPHASE 2: Martingale trades (CMFLIX losses only)");
            Console.WriteLine($"         Found {losses.Count} losses to trade\n");

            if (losses.Count == 0)
            {
                Console.WriteLine("No losses to trade - CMFLIX won all signals!");
            }
            else
            {
                Console.WriteLine("{0,-4} {1,-8} {2,-6} {3,-8} {4,-12} {5,-12} {6,-8} {7,-10}",
                    "#", "Asset", "Dir", "Entry@", "Entry", "Exit", "Result", "P&L");
                Console.WriteLine(new string('-', 80));

                var martingaleResults = new List<(string Asset, string Dir, DateTime EntryTime, decimal Entry, decimal Exit, bool Won, decimal PnL)>();

                foreach (var loss in losses)
                {
                    // Our entry is at CMFLIX expiry (T+15), exit at T+30
                    var ourEntryTime = loss.SignalTime.AddMinutes(15);
                    var ourExitTime = loss.SignalTime.AddMinutes(30);

                    // Skip if our trade hasn't expired yet
                    if (ourExitTime > now)
                    {
                        Console.WriteLine("{0,-4} {1,-8} {2,-6} {3,-8} -- OUR TRADE PENDING --",
                            loss.Num, loss.Asset, loss.Dir, ourEntryTime.ToString("HH:mm"));
                        continue;
                    }

                    var ourEntry = await derivClient.GetHistoricalPriceAsync(loss.Asset, ourEntryTime);
                    await Task.Delay(200);
                    var ourExit = await derivClient.GetHistoricalPriceAsync(loss.Asset, ourExitTime);
                    await Task.Delay(200);

                    if (ourEntry == null || ourExit == null)
                    {
                        Console.WriteLine("{0,-4} {1,-8} {2,-6} {3,-8} -- NO DATA --",
                            loss.Num, loss.Asset, loss.Dir, ourEntryTime.ToString("HH:mm"));
                        continue;
                    }

                    // Same direction as CMFLIX signal
                    var isCall = loss.Dir == "CALL";
                    var weWon = (isCall && ourExit > ourEntry) || (!isCall && ourExit < ourEntry);
                    var pnl = weWon ? stakeUsd * payoutPercent : -stakeUsd;

                    martingaleResults.Add((loss.Asset, loss.Dir, ourEntryTime, ourEntry.Value, ourExit.Value, weWon, pnl));

                    Console.WriteLine("{0,-4} {1,-8} {2,-6} {3,-8} {4,-12:F5} {5,-12:F5} {6,-8} {7,-10}",
                        loss.Num, loss.Asset, loss.Dir, ourEntryTime.ToString("HH:mm"),
                        ourEntry, ourExit, weWon ? "WIN" : "LOSS",
                        pnl >= 0 ? $"+${pnl:F2}" : $"-${Math.Abs(pnl):F2}");
                }

                // Summary
                if (martingaleResults.Count > 0)
                {
                    var wins = martingaleResults.Count(r => r.Won);
                    var martLosses = martingaleResults.Count - wins;
                    var winRate = (decimal)wins / martingaleResults.Count * 100;
                    var totalPnL = martingaleResults.Sum(r => r.PnL);

                    Console.WriteLine("\n" + new string('=', 80));
                    Console.WriteLine("MARTINGALE SUMMARY");
                    Console.WriteLine(new string('=', 80));
                    Console.WriteLine($"  CMFLIX Signals:    {cmflixResults.Count}");
                    Console.WriteLine($"  CMFLIX Wins:       {cmflixResults.Count(r => r.CmflixWon)}");
                    Console.WriteLine($"  CMFLIX Losses:     {losses.Count}");
                    Console.WriteLine();
                    Console.WriteLine($"  Our Trades:        {martingaleResults.Count}");
                    Console.WriteLine($"  Our Wins:          {wins}");
                    Console.WriteLine($"  Our Losses:        {martLosses}");
                    Console.WriteLine($"  Our Win Rate:      {winRate:F1}%");
                    Console.WriteLine($"  Our Total P&L:     {(totalPnL >= 0 ? "+" : "")}${totalPnL:F2}");
                    Console.WriteLine();

                    // Compare strategies
                    var cmflixPnL = cmflixResults.Sum(r => r.CmflixWon ? stakeUsd * payoutPercent : -stakeUsd);
                    Console.WriteLine("COMPARISON:");
                    Console.WriteLine($"  Following CMFLIX:  {(cmflixPnL >= 0 ? "+" : "")}${cmflixPnL:F2}");
                    Console.WriteLine($"  Martingale Only:   {(totalPnL >= 0 ? "+" : "")}${totalPnL:F2}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nBacktest FAILED: {ex.Message}");
            Log.Error(ex, "CMFLIX martingale backtest failed");
        }
        finally
        {
            await derivClient.DisconnectAsync();
        }

        Log.CloseAndFlush();
    }

    /// <summary>
    /// Run CMFLIX multi-day backtest for multiple signal lists.
    /// Usage: dotnet run -- --mode cmflix-multi
    /// </summary>
    private static async Task RunCmflixMultiDayBacktestAsync(string[] args, IConfiguration configuration, string contentRoot)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  CMFLIX MULTI-DAY BACKTEST (Original + Martingale)");
        Console.WriteLine("================================================================\n");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(contentRoot, "logs", "cmflix-multi-.txt"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        // Define all signal lists with their dates
        var signalLists = new List<(DateTime Date, string Label, string Signals)>
        {
            (new DateTime(2026, 1, 7), "Jan 07, 2026", @"* 09:55 - EUR/USD - CALL
* 10:25 - AUD/JPY - PUT
* 10:50 - EUR/JPY - CALL
* 10:55 - AUD/JPY - CALL
* 11:15 - EUR/USD - CALL
* 11:25 - AUD/JPY - CALL
* 11:25 - USD/CAD - PUT
* 11:45 - AUD/JPY - PUT
* 12:00 - USD/CAD - CALL
* 12:15 - EUR/USD - PUT
* 12:25 - EUR/GBP - CALL
* 12:45 - AUD/JPY - PUT
* 12:55 - EUR/USD - PUT
* 13:25 - AUD/JPY - CALL
* 13:30 - GBP/USD - CALL
* 13:40 - USD/CAD - PUT
* 13:55 - EUR/GBP - PUT
* 14:05 - EUR/USD - CALL
* 14:25 - EUR/GBP - PUT
* 14:35 - USD/CAD - PUT
* 14:45 - AUD/JPY - PUT
* 15:10 - EUR/USD - CALL
* 15:20 - EUR/GBP - CALL
* 15:30 - USD/CAD - PUT
* 15:45 - AUD/JPY - PUT
* 16:05 - EUR/USD - PUT
* 16:20 - GBP/USD - CALL"),

            (new DateTime(2026, 1, 6), "Jan 06, 2026", @"* 09:40 - EUR/USD - CALL
* 10:25 - AUD/JPY - PUT
* 10:50 - EUR/JPY - CALL
* 10:55 - AUD/JPY - CALL
* 11:15 - EUR/USD - CALL
* 11:25 - AUD/JPY - CALL
* 11:25 - USD/CAD - PUT
* 11:45 - AUD/JPY - PUT
* 12:00 - USD/CAD - CALL
* 12:15 - EUR/USD - PUT
* 12:25 - EUR/GBP - CALL
* 12:45 - AUD/JPY - PUT
* 12:55 - EUR/USD - PUT
* 13:25 - AUD/JPY - CALL
* 13:30 - GBP/USD - CALL
* 13:40 - USD/CAD - PUT
* 13:55 - EUR/GBP - PUT
* 14:05 - EUR/USD - CALL
* 14:25 - EUR/GBP - PUT
* 14:35 - USD/CAD - PUT
* 14:45 - AUD/JPY - PUT
* 15:10 - EUR/USD - CALL
* 15:20 - EUR/GBP - CALL
* 15:30 - USD/CAD - PUT
* 15:45 - AUD/JPY - PUT
* 16:05 - EUR/USD - PUT
* 16:20 - GBP/USD - CALL"),

            (new DateTime(2025, 12, 5), "Dec 05, 2025", @"* 09:00 - EUR/USD - CALL
* 09:20 - EUR/GBP - PUT
* 09:35 - EUR/JPY - CALL
* 10:10 - EUR/USD - PUT
* 10:25 - AUD/JPY - PUT
* 10:50 - EUR/JPY - CALL
* 10:55 - AUD/JPY - CALL
* 11:15 - EUR/USD - CALL
* 11:25 - AUD/JPY - CALL
* 11:25 - USD/CAD - PUT
* 11:45 - AUD/JPY - PUT
* 12:00 - USD/CAD - CALL
* 12:15 - EUR/USD - PUT
* 12:25 - EUR/GBP - CALL
* 12:45 - AUD/JPY - PUT
* 12:55 - EUR/USD - PUT
* 13:25 - AUD/JPY - CALL
* 13:30 - GBP/USD - CALL
* 13:40 - USD/CAD - PUT
* 13:55 - EUR/GBP - PUT
* 14:05 - EUR/USD - CALL
* 14:25 - EUR/GBP - PUT
* 14:35 - USD/CAD - PUT
* 14:45 - AUD/JPY - PUT
* 15:10 - EUR/USD - CALL
* 15:20 - EUR/GBP - CALL
* 15:30 - USD/CAD - PUT
* 15:45 - AUD/JPY - PUT
* 16:05 - EUR/USD - PUT
* 16:20 - GBP/USD - CALL"),

            (new DateTime(2025, 12, 4), "Dec 04, 2025 (OTC)", @"* 09:20 - EUR/USD - PUT
* 09:35 - EUR/JPY - CALL
* 09:45 - EUR/GBP - CALL
* 10:00 - USD/BRL - CALL
* 11:15 - AUD/JPY - PUT
* 12:25 - EUR/JPY - CALL
* 12:45 - EUR/JPY - PUT
* 12:55 - XAU/USD - PUT
* 13:25 - EUR/JPY - CALL
* 13:30 - XAU/USD - CALL
* 13:40 - USD/CAD - PUT
* 13:55 - EUR/JPY - PUT
* 14:05 - XAU/USD - CALL
* 14:25 - XAU/USD - PUT
* 14:35 - USD/BRL - PUT
* 14:50 - EUR/JPY - PUT
* 15:05 - XAU/USD - CALL
* 15:15 - AUD/JPY - CALL
* 15:30 - USD/BRL - PUT
* 15:45 - XAU/USD - PUT
* 16:05 - EUR/JPY - PUT
* 16:20 - GBP/USD - CALL
* 16:35 - XAU/USD - PUT
* 16:40 - EUR/JPY - PUT
* 16:50 - AUD/JPY - CALL
* 17:05 - XAU/USD - PUT
* 17:15 - AUD/JPY - PUT
* 17:35 - EUR/JPY - CALL")
        };

        // Build services
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog());
        services.AddSingleton<IConfiguration>(configuration);
        var derivConfig = new DerivConfig();
        configuration.GetSection("Deriv").Bind(derivConfig);
        services.AddSingleton(derivConfig);
        services.AddSingleton<IDerivClient, DerivClient>();
        services.AddSingleton<DerivCTrader.Application.Parsers.CmflixParser>();

        await using var provider = services.BuildServiceProvider();
        var derivClient = provider.GetRequiredService<IDerivClient>();
        var parser = provider.GetRequiredService<DerivCTrader.Application.Parsers.CmflixParser>();

        var stakeUsd = 20m;
        var payoutPercent = 0.85m;

        // Grand totals
        var grandTotalOriginalWins = 0;
        var grandTotalOriginalLosses = 0;
        var grandTotalOriginalPnL = 0m;
        var grandTotalMartingaleWins = 0;
        var grandTotalMartingaleLosses = 0;
        var grandTotalMartingalePnL = 0m;
        var grandTotalMartingaleTrades = 0;

        try
        {
            Console.WriteLine("Connecting to Deriv...");
            await derivClient.ConnectAsync();
            Console.WriteLine("Connected!\n");

            foreach (var (date, label, signalsText) in signalLists)
            {
                Console.WriteLine("\n" + new string('=', 80));
                Console.WriteLine($"  {label}");
                Console.WriteLine(new string('=', 80) + "\n");

                // Parse signals for this date
                var fullMessage = $"CMFLIX GOLD SIGNALS\n{date:dd/MM}\n5 MINUTOS\n\n{signalsText}";
                var signals = parser.ParseBatch(fullMessage, overrideDate: date);

                if (signals.Count == 0)
                {
                    Console.WriteLine("  No signals parsed for this date.");
                    continue;
                }

                Console.WriteLine($"Parsed {signals.Count} signals\n");

                // Evaluate all signals
                var results = new List<(int Num, string Asset, string Dir, DateTime Time, decimal Entry, decimal Exit, bool Won)>();

                Console.WriteLine("{0,-4} {1,-10} {2,-6} {3,-8} {4,-12} {5,-12} {6,-8}",
                    "#", "Asset", "Dir", "Time", "Entry", "Exit", "Result");
                Console.WriteLine(new string('-', 70));

                int num = 0;
                foreach (var signal in signals)
                {
                    num++;
                    var entryTime = signal.ScheduledAtUtc!.Value;
                    var exitTime = entryTime.AddMinutes(15);

                    var entry = await derivClient.GetHistoricalPriceAsync(signal.Asset, entryTime);
                    await Task.Delay(150);
                    var exit = await derivClient.GetHistoricalPriceAsync(signal.Asset, exitTime);
                    await Task.Delay(150);

                    if (entry == null || exit == null)
                    {
                        Console.WriteLine("{0,-4} {1,-10} {2,-6} {3,-8} -- NO DATA --",
                            num, signal.Asset,
                            signal.Direction == DerivCTrader.Domain.Enums.TradeDirection.Call ? "CALL" : "PUT",
                            entryTime.ToString("HH:mm"));
                        continue;
                    }

                    var isCall = signal.Direction == DerivCTrader.Domain.Enums.TradeDirection.Call;
                    var won = (isCall && exit > entry) || (!isCall && exit < entry);

                    results.Add((num, signal.Asset, isCall ? "CALL" : "PUT", entryTime, entry.Value, exit.Value, won));

                    Console.WriteLine("{0,-4} {1,-10} {2,-6} {3,-8} {4,-12:F5} {5,-12:F5} {6,-8}",
                        num, signal.Asset, isCall ? "CALL" : "PUT", entryTime.ToString("HH:mm"),
                        entry, exit, won ? "WIN" : "LOSS");
                }

                if (results.Count == 0)
                {
                    Console.WriteLine("\n  No valid results for this date.");
                    continue;
                }

                // Original strategy summary
                var origWins = results.Count(r => r.Won);
                var origLosses = results.Count - origWins;
                var origPnL = results.Sum(r => r.Won ? stakeUsd * payoutPercent : -stakeUsd);

                Console.WriteLine($"\n--- ORIGINAL (Following CMFLIX) ---");
                Console.WriteLine($"  Trades: {results.Count}, Wins: {origWins}, Losses: {origLosses}");
                Console.WriteLine($"  Win Rate: {(decimal)origWins / results.Count * 100:F1}%, P&L: {(origPnL >= 0 ? "+" : "")}${origPnL:F2}");

                grandTotalOriginalWins += origWins;
                grandTotalOriginalLosses += origLosses;
                grandTotalOriginalPnL += origPnL;

                // Martingale: trade after losses
                var losses = results.Where(r => !r.Won).ToList();
                if (losses.Count > 0)
                {
                    Console.WriteLine($"\n--- MARTINGALE (Enter after CMFLIX loses) ---");
                    Console.WriteLine($"  CMFLIX Losses: {losses.Count} → Our entries\n");

                    var martResults = new List<(bool Won, decimal PnL)>();

                    foreach (var loss in losses)
                    {
                        var ourEntry = loss.Exit; // Our entry = CMFLIX exit
                        var ourExitTime = loss.Time.AddMinutes(30);
                        var ourExit = await derivClient.GetHistoricalPriceAsync(loss.Asset, ourExitTime);
                        await Task.Delay(150);

                        if (ourExit == null) continue;

                        var isCall = loss.Dir == "CALL";
                        var weWon = (isCall && ourExit > ourEntry) || (!isCall && ourExit < ourEntry);
                        var pnl = weWon ? stakeUsd * payoutPercent : -stakeUsd;
                        martResults.Add((weWon, pnl));

                        Console.WriteLine("  {0,-10} {1,-6} Entry@{2} → {3} ({4})",
                            loss.Asset, loss.Dir, loss.Time.AddMinutes(15).ToString("HH:mm"),
                            weWon ? "WIN" : "LOSS", pnl >= 0 ? $"+${pnl:F2}" : $"-${Math.Abs(pnl):F2}");
                    }

                    if (martResults.Count > 0)
                    {
                        var martWins = martResults.Count(r => r.Won);
                        var martLosses = martResults.Count - martWins;
                        var martPnL = martResults.Sum(r => r.PnL);

                        Console.WriteLine($"\n  Martingale: {martResults.Count} trades, {martWins} wins, {martLosses} losses");
                        Console.WriteLine($"  Win Rate: {(decimal)martWins / martResults.Count * 100:F1}%, P&L: {(martPnL >= 0 ? "+" : "")}${martPnL:F2}");

                        grandTotalMartingaleWins += martWins;
                        grandTotalMartingaleLosses += martLosses;
                        grandTotalMartingalePnL += martPnL;
                        grandTotalMartingaleTrades += martResults.Count;
                    }
                }
                else
                {
                    Console.WriteLine("\n--- MARTINGALE: No losses to trade! ---");
                }
            }

            // Grand summary
            Console.WriteLine("\n" + new string('=', 80));
            Console.WriteLine("  GRAND SUMMARY (ALL DAYS)");
            Console.WriteLine(new string('=', 80));

            var totalOrigTrades = grandTotalOriginalWins + grandTotalOriginalLosses;
            var totalMartTrades = grandTotalMartingaleWins + grandTotalMartingaleLosses;

            Console.WriteLine($"\n  ORIGINAL (Following CMFLIX):");
            Console.WriteLine($"    Total Trades: {totalOrigTrades}");
            Console.WriteLine($"    Wins: {grandTotalOriginalWins}, Losses: {grandTotalOriginalLosses}");
            Console.WriteLine($"    Win Rate: {(totalOrigTrades > 0 ? (decimal)grandTotalOriginalWins / totalOrigTrades * 100 : 0):F1}%");
            Console.WriteLine($"    Total P&L: {(grandTotalOriginalPnL >= 0 ? "+" : "")}${grandTotalOriginalPnL:F2}");

            Console.WriteLine($"\n  MARTINGALE (Enter after loss):");
            Console.WriteLine($"    Total Trades: {totalMartTrades}");
            Console.WriteLine($"    Wins: {grandTotalMartingaleWins}, Losses: {grandTotalMartingaleLosses}");
            Console.WriteLine($"    Win Rate: {(totalMartTrades > 0 ? (decimal)grandTotalMartingaleWins / totalMartTrades * 100 : 0):F1}%");
            Console.WriteLine($"    Total P&L: {(grandTotalMartingalePnL >= 0 ? "+" : "")}${grandTotalMartingalePnL:F2}");

            Console.WriteLine("\n" + new string('=', 80));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nBacktest FAILED: {ex.Message}");
            Log.Error(ex, "CMFLIX multi-day backtest failed");
        }
        finally
        {
            await derivClient.DisconnectAsync();
        }

        Log.CloseAndFlush();
    }

    /// <summary>
    /// Run import mode for historical data.
    /// Usage: dotnet run -- --import=C:\data\csvs --intraday
    /// </summary>
    private static async Task RunImportModeAsync(string[] args, IConfiguration configuration, string importPath)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("  HISTORICAL DATA IMPORT MODE");
        Console.WriteLine("========================================\n");

        var isIntraday = HasFlag(args, "--intraday");
        var symbol = TryGetStringArg(args, "--symbol");

        // Configure logging
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .CreateLogger();

        // Build services
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog());
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IBacktestRepository, SqlServerBacktestRepository>();
        services.AddSingleton<HistoricalDataImporter>();

        await using var provider = services.BuildServiceProvider();
        var importer = provider.GetRequiredService<HistoricalDataImporter>();

        try
        {
            if (Directory.Exists(importPath))
            {
                // Import all files from directory
                await importer.ImportFromDirectoryAsync(importPath, isIntraday);
            }
            else if (File.Exists(importPath))
            {
                // Import single file
                if (string.IsNullOrEmpty(symbol))
                {
                    Console.WriteLine("ERROR: --symbol required when importing single file");
                    Console.WriteLine("Usage: dotnet run -- --import=GS.csv --symbol=GS");
                    return;
                }

                if (isIntraday)
                {
                    await importer.ImportIntradayCsvAsync(importPath, symbol);
                }
                else
                {
                    await importer.ImportYahooFinanceCsvAsync(importPath, symbol);
                }
            }
            else
            {
                Console.WriteLine($"ERROR: Path not found: {importPath}");
            }

            // Show coverage after import
            await importer.PrintDataCoverageAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nImport FAILED: {ex.Message}");
            Log.Error(ex, "Import failed");
        }

        Log.CloseAndFlush();
    }

    /// <summary>
    /// Run fetch-data mode using Massive API.
    /// Usage: dotnet run -- --fetch-data --from=2024-01-01 --to=2024-06-30
    /// </summary>
    private static async Task RunFetchDataModeAsync(string[] args, IConfiguration configuration, string contentRoot)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("  MASSIVE API DATA FETCH MODE");
        Console.WriteLine("========================================\n");

        var fromDate = TryGetDateArg(args, "--from");
        var toDate = TryGetDateArg(args, "--to");
        var searchTickers = HasFlag(args, "--search");

        if (!searchTickers && (!fromDate.HasValue || !toDate.HasValue))
        {
            Console.WriteLine("ERROR: --from and --to dates are required");
            Console.WriteLine("\nUsage:");
            Console.WriteLine("  dotnet run -- --fetch-data --from=2024-01-01 --to=2024-06-30");
            Console.WriteLine("\nOptions:");
            Console.WriteLine("  --gs-ticker=GS      GS ticker (default: GS)");
            Console.WriteLine("  --dow-ticker=DIA    Dow ticker for WS30 (default: DIA - Dow Jones ETF)");
            Console.WriteLine("  --single-day        Fetch one day with verbose output (for testing)");
            Console.WriteLine("  --search            Search for available tickers");
            return;
        }

        // Check API key
        var apiKey = configuration["MassiveAPI:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("ERROR: MassiveAPI:ApiKey not configured in appsettings.json");
            Console.WriteLine("\nAdd to appsettings.Production.json:");
            Console.WriteLine("  \"MassiveAPI\": {");
            Console.WriteLine("    \"ApiKey\": \"your-api-key-here\",");
            Console.WriteLine("    \"BaseUrl\": \"https://api.polygon.io\"");
            Console.WriteLine("  }");
            return;
        }

        // Configure logging
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(contentRoot, "logs", "fetch-data-.txt"),
                rollingInterval: RollingInterval.Day)
            .CreateLogger();

        // Build services
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog());
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IBacktestRepository, SqlServerBacktestRepository>();
        services.AddHttpClient<IMassiveApiService, MassiveApiService>();
        services.AddSingleton<MassiveDataFetcher>();

        await using var provider = services.BuildServiceProvider();
        var fetcher = provider.GetRequiredService<MassiveDataFetcher>();

        var gsTicker = TryGetStringArg(args, "--gs-ticker") ?? "GS";
        var dowTicker = TryGetStringArg(args, "--dow-ticker") ?? "DIA";  // DIA ETF tracks Dow Jones
        var singleDay = HasFlag(args, "--single-day");

        try
        {
            // Search mode
            if (searchTickers)
            {
                await fetcher.SearchAllTickersAsync();
                Log.CloseAndFlush();
                return;
            }

            int totalCandles;

            if (singleDay)
            {
                // Single day test with verbose output
                totalCandles = await fetcher.FetchSingleDayAsync(fromDate!.Value, gsTicker, dowTicker);
            }
            else
            {
                // Full range fetch
                totalCandles = await fetcher.FetchDataForBacktestAsync(fromDate!.Value, toDate!.Value, gsTicker, dowTicker);
            }

            Console.WriteLine($"\nFetch completed. Total candles: {totalCandles}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nFetch FAILED: {ex.Message}");
            Log.Error(ex, "Fetch failed");
        }

        Log.CloseAndFlush();
    }
}
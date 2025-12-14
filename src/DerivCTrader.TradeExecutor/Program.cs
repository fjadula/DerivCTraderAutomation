using DerivCTrader.Application.Interfaces;
using DerivCTrader.Infrastructure.CTrader.Extensions;
using DerivCTrader.Infrastructure.Persistence;
using DerivCTrader.Infrastructure.Deriv;
using DerivCTrader.Infrastructure.ExpiryCalculation;
using DerivCTrader.Infrastructure.Notifications;
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

                    // Telegram notifications (trade fill/close)
                    services.AddSingleton<ITelegramNotifier, TelegramNotifier>();

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

                    // Register DerivBinaryExecutorService as singleton and hosted service for event-driven injection
                    services.AddSingleton<DerivBinaryExecutorService>();
                    services.AddHostedService(sp => sp.GetRequiredService<DerivBinaryExecutorService>());

                    services.AddHostedService<BinaryExecutionService>();          // Process pure binary signals -> Deriv
                    services.AddHostedService<OutcomeMonitorService>();

                    // TODO: Wire up SignalSqlNotificationService.SignalChanged to trigger processing in background services

                    // Bridge: Hosted service to wire up SQL notification to DerivBinaryExecutorService
                    services.AddHostedService<SqlToDerivExecutorBridgeService>();

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
            Console.WriteLine("✅ Deriv Binary Executor Service (Queue)");
            Console.WriteLine("✅ Binary Execution Service (Pure Binary)");
            Console.WriteLine("✅ Outcome Monitor Service");
            Console.WriteLine("========================================\n");
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
}
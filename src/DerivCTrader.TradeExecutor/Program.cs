using DerivCTrader.Application.Interfaces;
using DerivCTrader.Infrastructure.Database;
using DerivCTrader.Infrastructure.Deriv;
using DerivCTrader.Infrastructure.ExpiryCalculation;
using DerivCTrader.TradeExecutor.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

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

                    // Deriv client
                    services.AddSingleton<IDerivClient, DerivClient>();

                    // Binary expiry calculator
                    services.AddSingleton<IBinaryExpiryCalculator, BinaryExpiryCalculator>();

                    // Register background services
                    services.AddHostedService<BinaryExecutionService>();
                    services.AddHostedService<OutcomeMonitorService>();

                    Log.Information("Services registered successfully");
                })
                .Build();

            Console.WriteLine("\n========================================_");
            Console.WriteLine("  SERVICES REGISTERED");
            Console.WriteLine("========================================");
            Console.WriteLine("✅ Database Repository");
            Console.WriteLine("✅ Deriv WebSocket Client");
            Console.WriteLine("✅ Binary Execution Service");
            Console.WriteLine("✅ Outcome Monitor Service");
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
}
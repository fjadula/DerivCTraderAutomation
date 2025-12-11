using DerivCTrader.Application.Interfaces;
using DerivCTrader.Application.Parsers;
using DerivCTrader.Infrastructure.CTrader;
using DerivCTrader.Infrastructure.CTrader.Extensions;
using DerivCTrader.Infrastructure.Persistence;
using DerivCTrader.SignalScraper.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DerivCTrader.SignalScraper;

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
        Console.WriteLine("  DERIVCTRADER SIGNAL SCRAPER");
        Console.WriteLine("========================================");
        Console.WriteLine($"📁 Working Directory: {contentRoot}");
        Console.WriteLine();

        // Build configuration
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(contentRoot);

        var productionFile = Path.Combine(contentRoot, "appsettings.Production.json");
        if (File.Exists(productionFile))
        {
            configBuilder.AddJsonFile("appsettings.Production.json", optional: false, reloadOnChange: true);
            Console.WriteLine("✅ Using appsettings.Production.json");
        }
        else
        {
            configBuilder.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            Console.WriteLine("⚠️  Using appsettings.json");
        }

        var configuration = configBuilder.Build();

        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .WriteTo.File(
                Path.Combine(contentRoot, "logs", "signalscraper-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            .CreateLogger();

        try
        {
            Log.Information("Starting Deriv cTrader Signal Scraper...");
            Log.Information("Working directory: {ContentRoot}", contentRoot);
            Console.WriteLine("\n=== BUILDING HOST ===\n");

            var host = Host.CreateDefaultBuilder(args)
                .UseContentRoot(contentRoot)
                .UseSerilog()
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<IConfiguration>(configuration);
                    services.AddSingleton<ITradeRepository, SqlServerTradeRepository>();
                    services.AddCTraderServices(configuration);
                    // Register signal parsers - ORDER MATTERS!
                    // Test channel parser MUST be registered FIRST
                    services.AddSingleton<ISignalParser, TestChannelParser>();
                    services.AddSingleton<ISignalParser, VipFxParser>();
                    services.AddSingleton<ISignalParser, PerfectFxParser>();
                    services.AddSingleton<ISignalParser, VipChannelParser>();
                    services.AddSingleton<ISignalParser, TradingHubVipParser>();
                    services.AddSingleton<ISignalParser, SyntheticIndicesParser>();
                    services.AddSingleton<ISignalParser, NewStratsParser>();
                    services.AddTransient<CTraderConnectionTest>();
                    services.AddHostedService<TelegramSignalScraperService>();
                    Log.Information("Services registered successfully");
                })
                .Build();

            Console.WriteLine("✅ HOST BUILT SUCCESSFULLY\n");
            Log.Information("Host built successfully");

            // Test cTrader connection
            Console.WriteLine("==============================================");
            Console.WriteLine("  TESTING CTRADER CONNECTION");
            Console.WriteLine("==============================================\n");

            try
            {
                var testService = host.Services.GetRequiredService<CTraderConnectionTest>();
                await testService.RunTestAsync();
                Console.WriteLine("✅ cTrader connection test passed!\n");
            }
            catch (Exception testEx)
            {
                Log.Error(testEx, "cTrader connection test failed");
                Console.WriteLine($"\n❌ cTrader test FAILED: {testEx.Message}");
                if (Environment.UserInteractive)
                {
                    Console.WriteLine("\nPress any key to exit...");
                    Console.ReadKey();
                }
                return;
            }

            Log.Information("Starting Telegram Signal Scraper service...");
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n❌ FATAL ERROR: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Log.Fatal(ex, "Application terminated unexpectedly");
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
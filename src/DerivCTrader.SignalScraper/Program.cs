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
        // Determine content root (where appsettings*.json + logs folder live)
        // dotnet watch may run from solution root, so we resolve a usable base path.
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

        static (string basePath, string fileName) ResolveSettingsBasePath(string initialBasePath)
        {
            var candidates = new List<string>
            {
                initialBasePath,
                Path.Combine(initialBasePath, "src", "DerivCTrader.SignalScraper"),
                AppContext.BaseDirectory
            };

            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
                    continue;

                if (File.Exists(Path.Combine(candidate, "appsettings.Production.json")))
                    return (candidate, "appsettings.Production.json");
                if (File.Exists(Path.Combine(candidate, "appsettings.json")))
                    return (candidate, "appsettings.json");
            }

            throw new FileNotFoundException(
                "Could not find appsettings.Production.json or appsettings.json. " +
                "Run from the SignalScraper folder or pass --contentRoot <path>.");
        }

        try
        {
            var resolved = ResolveSettingsBasePath(contentRoot);
            contentRoot = resolved.basePath;
            Directory.SetCurrentDirectory(contentRoot);

            Console.WriteLine("========================================");
            Console.WriteLine("  DERIVCTRADER SIGNAL SCRAPER");
            Console.WriteLine("========================================");
            Console.WriteLine($"📁 Working Directory: {contentRoot}");
            Console.WriteLine();

            // Build configuration
            var configBuilder = new ConfigurationBuilder()
                .SetBasePath(contentRoot)
                .AddJsonFile(resolved.fileName, optional: false, reloadOnChange: true);

            Console.WriteLine($"✅ Using {resolved.fileName}");

            var configuration = configBuilder.Build();

            // Configure Serilog
            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .WriteTo.File(
                    Path.Combine(contentRoot, "logs", "signalscraper-.txt"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 1,
                    fileSizeLimitBytes: 52428800, // 50MB limit
                    rollOnFileSizeLimit: true)
                .CreateLogger();

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
                    services.AddSingleton<IDashaTradeRepository, SqlServerDashaTradeRepository>(); // DashaTrade selective martingale
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
                    services.AddSingleton<ISignalParser, PipsMoveParser>();
                    services.AddSingleton<ISignalParser, FxTradingProfessorParser>();
                    services.AddSingleton<ISignalParser, ChartSenseParser>();
                    services.AddSingleton<ISignalParser, AFXGoldParser>();
                    services.AddSingleton<ISignalParser, DerivPlusParser>();
                    services.AddSingleton<ISignalParser, DashaTradeParser>();

                    // CMFLIX batch parser (not ISignalParser - uses batch parsing)
                    services.AddSingleton<CmflixParser>();

                    // IzintzikaDeriv batch parser (not ISignalParser - uses batch parsing)
                    services.AddSingleton<IzintzikaDerivParser>();

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
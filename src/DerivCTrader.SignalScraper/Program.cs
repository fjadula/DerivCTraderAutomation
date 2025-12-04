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
        Console.WriteLine("========================================");
        Console.WriteLine("  DERIVCTRADER SIGNAL SCRAPER");
        Console.WriteLine("========================================\n");

        // Build configuration - prioritize Production file if it exists
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory());

        var productionFile = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.Production.json");
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

        // Configure Serilog BEFORE using it
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .CreateLogger();

        try
        {
            Log.Information("Starting Deriv cTrader Signal Scraper...");
            Console.WriteLine("\n=== BUILDING HOST ===\n");

            var host = Host.CreateDefaultBuilder(args)
                .UseSerilog()
                .ConfigureServices((context, services) =>
                {
                    // Register configuration
                    services.AddSingleton<IConfiguration>(configuration);

                    // Register repositories
                    services.AddSingleton<ITradeRepository, SqlServerTradeRepository>();

                    // Register cTrader services
                    services.AddCTraderServices(configuration);

                    // Register signal parsers
                    services.AddSingleton<ISignalParser, VipFxParser>();
                    services.AddSingleton<ISignalParser, PerfectFxParser>();
                    services.AddSingleton<ISignalParser, VipChannelParser>();

                    // Register test class
                    services.AddTransient<CTraderConnectionTest>();

                    // Register background services
                    services.AddHostedService<TelegramSignalScraperService>();

                    Log.Information("Services registered successfully");
                })
                .Build();

            Console.WriteLine("✅ HOST BUILT SUCCESSFULLY\n");
            Log.Information("Host built successfully");

            // 🧪 RUN CTRADER CONNECTION TEST FIRST
            Console.WriteLine("==============================================");
            Console.WriteLine("  STEP 1: TESTING CTRADER CONNECTION");
            Console.WriteLine("==============================================\n");
            
            try
            {
                var testService = host.Services.GetRequiredService<CTraderConnectionTest>();
                await testService.RunTestAsync();
                
                Console.WriteLine("✅ cTrader connection test passed!");
                Console.WriteLine("\n==============================================");
                Console.WriteLine("  STEP 2: STARTING TELEGRAM MONITORING");
                Console.WriteLine("==============================================\n");
            }
            catch (Exception testEx)
            {
                Console.WriteLine($"\n❌ cTrader connection test FAILED!");
                Console.WriteLine($"Error: {testEx.Message}");
                Console.WriteLine("\n⚠️  Cannot start application without valid cTrader connection.");
                Console.WriteLine("Please check your credentials in appsettings.Production.json:\n");
                Console.WriteLine("  - ClientId: Should start with a number");
                Console.WriteLine("  - ClientSecret: Long alphanumeric string");
                Console.WriteLine("  - AccessToken: Valid OAuth token (expires in 30 days)");
                Console.WriteLine("  - DemoAccountId: Your demo account ID (e.g., 2295141)");
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                return;
            }

            // Start the main application
            Log.Information("Starting Telegram Signal Scraper service...");
            await host.RunAsync();

            Console.WriteLine("\n=== APPLICATION STOPPED ===");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n❌ FATAL ERROR: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Log.Fatal(ex, "Application terminated unexpectedly");
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
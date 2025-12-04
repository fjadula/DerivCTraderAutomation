using DerivCTrader.Application.Interfaces;
using DerivCTrader.Application.Parsers;
using DerivCTrader.Infrastructure.ExpiryCalculation;
using DerivCTrader.Infrastructure.Persistence;
using DerivCTrader.Infrastructure.Trading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Settings.Configuration;

namespace DerivCTrader.TradeExecutor;

class Program
{
    static async Task Main(string[] args)
    {
        // Build configuration - prioritize Production file if it exists
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory());

        // Try Production file first (your actual credentials)
        var productionFile = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.Production.json");
        if (File.Exists(productionFile))
        {
            configBuilder.AddJsonFile("appsettings.Production.json", optional: false, reloadOnChange: true);
            Log.Information("Using appsettings.Production.json");
        }
        else
        {
            // Fallback to base file
            configBuilder.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            Log.Information("Using appsettings.json");
        }

        var configuration = configBuilder.Build();

        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .CreateLogger();

        try
        {
            Log.Information("Starting Deriv cTrader Trade Executor...");

            var host = Host.CreateDefaultBuilder(args)
                .UseSerilog()
                .ConfigureServices((context, services) =>
                {
                    // Register configuration
                    services.AddSingleton<IConfiguration>(configuration);

                    // Register repositories
                    services.AddSingleton<ITradeRepository, SqlServerTradeRepository>();

                    // Register clients
                    services.AddSingleton<IDerivClient, DerivWebSocketClient>();
                    // TODO: Add ICTraderClient when implemented
                    // services.AddSingleton<ICTraderClient, CTraderWebSocketClient>();

                    // Register services
                    services.AddSingleton<BinaryExpiryCalculator>();

                    // TODO: Register background services when ready
                    // services.AddHostedService<CTraderMonitorService>();
                    // services.AddHostedService<BinaryExecutionService>();
                    // services.AddHostedService<QueueMatchingService>();

                    Log.Information("Trade Executor configured. Background services will be added as they're implemented.");
                })
                .Build();

            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Trade Executor terminated unexpectedly");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
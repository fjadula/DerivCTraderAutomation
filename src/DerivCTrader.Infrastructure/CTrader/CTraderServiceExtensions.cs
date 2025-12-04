using DerivCTrader.Infrastructure.CTrader.Interfaces;
using DerivCTrader.Infrastructure.CTrader.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;

namespace DerivCTrader.Infrastructure.CTrader.Extensions;

public static class CTraderServiceExtensions
{
    public static IServiceCollection AddCTraderServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Create config manually to handle culture-specific parsing
        var section = configuration.GetSection("CTrader");

        var cTraderConfig = new CTraderConfig
        {
            ClientId = section["ClientId"] ?? string.Empty,
            ClientSecret = section["ClientSecret"] ?? string.Empty,
            AccessToken = section["AccessToken"] ?? string.Empty,
            Environment = section["Environment"] ?? "Demo",
            DemoAccountId = long.Parse(section["DemoAccountId"] ?? "0"),
            LiveAccountId = long.Parse(section["LiveAccountId"] ?? "0"),

            // 🔧 FIX: Parse decimal with InvariantCulture
            DefaultLotSize = double.Parse(
                section["DefaultLotSize"] ?? "0.2",
                CultureInfo.InvariantCulture),

            WebSocketUrl = section["WebSocketUrl"] ?? "wss://demo.ctraderapi.com",
            HeartbeatIntervalSeconds = int.Parse(section["HeartbeatIntervalSeconds"] ?? "25"),
            ConnectionTimeoutSeconds = int.Parse(section["ConnectionTimeoutSeconds"] ?? "30"),
            MessageTimeoutSeconds = int.Parse(section["MessageTimeoutSeconds"] ?? "10")
        };

        services.AddSingleton(cTraderConfig);
        services.AddSingleton<ICTraderClient, CTraderClient>();
        services.AddSingleton<ICTraderOrderManager, CTraderOrderManager>();

        return services;
    }
}
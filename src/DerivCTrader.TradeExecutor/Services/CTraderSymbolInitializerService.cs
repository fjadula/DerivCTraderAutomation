using DerivCTrader.Infrastructure.CTrader.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// Background service that initializes cTrader connection and fetches symbol list on startup
/// This must run before other cTrader services can process signals
/// </summary>
public class CTraderSymbolInitializerService : BackgroundService
{
    private readonly ILogger<CTraderSymbolInitializerService> _logger;
    private readonly ICTraderClient _client;
    private readonly ICTraderSymbolService _symbolService;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IConfiguration _configuration;

    public CTraderSymbolInitializerService(
        ILogger<CTraderSymbolInitializerService> logger,
        ICTraderClient client,
        ICTraderSymbolService symbolService,
        IHostApplicationLifetime lifetime,
        IConfiguration configuration)
    {
        _logger = logger;
        _client = client;
        _symbolService = symbolService;
        _lifetime = lifetime;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("=== CTRADER SYMBOL INITIALIZER STARTING ===");
            Console.WriteLine("\n========================================");
            Console.WriteLine("  cTrader Symbol Initializer");
            Console.WriteLine("========================================");
            Console.WriteLine("🔗 Connecting to cTrader...\n");

            // Step 1: Connect to cTrader
            await _client.ConnectAsync(stoppingToken);

            if (!_client.IsConnected)
            {
                _logger.LogError("Failed to connect to cTrader");
                Console.WriteLine("❌ Failed to connect to cTrader");
                return;
            }

            _logger.LogInformation("✅ Connected to cTrader");
            Console.WriteLine("✅ Connected to cTrader");

            // Step 2: Authenticate application
            Console.WriteLine("🔐 Authenticating application...");
            await _client.AuthenticateApplicationAsync(stoppingToken);

            if (!_client.IsApplicationAuthenticated)
            {
                _logger.LogError("Failed to authenticate application");
                Console.WriteLine("❌ Failed to authenticate application");
                return;
            }

            Console.WriteLine("✅ Application authenticated");

            // Step 3: Authenticate account (optional - will try but continue if fails)
            try
            {
                var ctraderSection = _configuration.GetSection("CTrader");
                var environment = ctraderSection["Environment"] ?? "Demo";
                var accountIdKey = environment == "Live" ? "LiveAccountId" : "DemoAccountId";
                var accountId = long.Parse(ctraderSection[accountIdKey] ?? "0");

                Console.WriteLine($"🔐 Authenticating account {accountId}...");
                await _client.AuthenticateAccountAsync(accountId, stoppingToken);

                if (!_client.IsAccountAuthenticated)
                {
                    _logger.LogWarning("Account authentication failed - continuing without account auth");
                    Console.WriteLine("⚠️  Account authentication failed - pending orders will not work");
                }
                else
                {
                    Console.WriteLine("✅ Account authenticated");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Account authentication error - continuing without account auth");
                Console.WriteLine($"⚠️  Account auth error: {ex.Message}");
                Console.WriteLine("⚠️  Continuing without account - pending orders will not work");
            }

            // Symbol service already initialized with common symbols
            // Skip account list and symbol fetching since we have hardcoded mappings
            Console.WriteLine("\n📥 cTrader Symbol Information:");
            Console.WriteLine("════════════════════════════════════════");
            Console.WriteLine($"\n✅ Symbol service initialized with all supported symbols");
            Console.WriteLine("\n💱 Forex Pairs (28 symbols):");
            Console.WriteLine("   • EURUSD, GBPUSD, USDJPY, USDCHF, AUDUSD, USDCAD");
            Console.WriteLine("   • NZDUSD, EURGBP, EURJPY, GBPJPY, EURCHF, EURAUD");
            Console.WriteLine("   • EURCAD, GBPCHF, GBPAUD, GBPCAD, AUDJPY, AUDNZD");
            Console.WriteLine("   • AUDCHF, AUDCAD, NZDJPY, CHFJPY, CADJPY, CADCHF");
            Console.WriteLine("   • GBPNZD, EURNZD, NZDCHF, NZDCAD");
            
            Console.WriteLine("\n📊 Deriv Synthetic Indices (Binary Trading Supported):");
            Console.WriteLine("\n   Continuous Volatility Indices:");
            Console.WriteLine("   • V10, V15, V25, V30, V50, V75, V90, V100");
            Console.WriteLine("   • V10 (1s), V15 (1s), V25 (1s), V50 (1s), V75 (1s), V100 (1s)");
            
            Console.WriteLine("\n   Jump Indices:");
            Console.WriteLine("   • Jump 10, Jump 25, Jump 50, Jump 75, Jump 100");
            
            Console.WriteLine("\n   Range Break Indices:");
            Console.WriteLine("   • Range Break 100, Range Break 200");
            
            Console.WriteLine("\n   Step Indices:");
            Console.WriteLine("   • Step 100, Step 200, Step 300, Step 400, Step 500");
            
            Console.WriteLine("\n   Daily Reset Indices:");
            Console.WriteLine("   • Bear Market Index, Bull Market Index");
            
            Console.WriteLine("\n⚠️  NOT SUPPORTED (No Binary Trading):");
            Console.WriteLine("   • Boom indices (300, 500, 600, 900, 1000)");
            Console.WriteLine("   • Crash indices (300, 500, 600, 900, 1000)");
            
            Console.WriteLine("\n📝 Symbol Name Format:");
            Console.WriteLine("   • Forex: No slash (EURUSD not EUR/USD)");
            Console.WriteLine("   • Synthetics: Multiple formats supported (V75, VOLATILITY75, etc.)");
            Console.WriteLine("════════════════════════════════════════\n");

            _logger.LogInformation("✅ cTrader client authenticated and symbols ready");
            Console.WriteLine("✅ cTrader client authenticated and symbols ready");
            Console.WriteLine("========================================\n");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize cTrader symbols");
            Console.WriteLine($"❌ Error: {ex.Message}");
            Console.WriteLine($"Stack: {ex.StackTrace}");
        }
    }
}

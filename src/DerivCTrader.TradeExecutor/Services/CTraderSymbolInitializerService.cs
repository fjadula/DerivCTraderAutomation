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

                    // Optional: Reconcile can be enabled via config.
                    // Some environments drop the TCP connection immediately after reconcile.
                    var enableReconcile = _configuration.GetValue("CTrader:EnableReconcile", false);
                    if (enableReconcile)
                    {
                        Console.WriteLine("🔄 Reconciling account stream...");
                        var reconciled = await _client.ReconcileAsync(stoppingToken);
                        if (!reconciled)
                        {
                            Console.WriteLine("⚠️  Reconcile did not complete; some event streams may not work");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Account authentication error - continuing without account auth");
                Console.WriteLine($"⚠️  Account auth error: {ex.Message}");
                Console.WriteLine("⚠️  Continuing without account - pending orders will not work");
            }

            // Step 4: Fetch actual symbol list (dynamic IDs)
            if (_client.IsAccountAuthenticated)
            {
                Console.WriteLine("\n📥 Fetching cTrader symbols (dynamic IDs)...");
                try
                {
                    await _symbolService.InitializeAsync();
                    Console.WriteLine("✅ Symbol list fetched from cTrader");
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Failed to fetch symbol list from cTrader; stopping to avoid wrong trades");
                    Console.WriteLine($"❌ Failed to fetch symbols: {ex.Message}");
                    Console.WriteLine("❌ Stopping application to prevent trading with incorrect SymbolId mappings");
                    _lifetime.StopApplication();
                    return;
                }
            }
            else
            {
                _logger.LogWarning("Skipping symbol fetch because account is not authenticated");
                Console.WriteLine("⚠️  Skipping symbol fetch (account not authenticated)");
            }

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

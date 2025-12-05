using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using DerivCTrader.Infrastructure.CTrader.Interfaces;
using DerivCTrader.Infrastructure.CTrader.Models;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.SignalScraper;

/// <summary>
/// Test class to verify cTrader connection and credentials
/// </summary>
public class CTraderConnectionTest
{
    private readonly ICTraderClient _client;
    private readonly ICTraderOrderManager _orderManager;
    private readonly ILogger<CTraderConnectionTest> _logger;

    public CTraderConnectionTest(
        ICTraderClient client,
        ICTraderOrderManager orderManager,
        ILogger<CTraderConnectionTest> logger)
    {
        _client = client;
        _orderManager = orderManager;
        _logger = logger;
    }

    public async Task RunTestAsync()
    {
        try
        {
            Console.WriteLine("========================================");
            Console.WriteLine("  CTRADER CONNECTION TEST");
            Console.WriteLine("========================================");

            // Step 1: Connect
            Console.WriteLine("\n1️⃣ Connecting to cTrader...");
            await _client.ConnectAsync();
            
            // Wait a bit for connection to stabilize
            await Task.Delay(2000);
            
            if (_client.IsConnected)
            {
                Console.WriteLine("   ✅ Connected!");
            }
            else
            {
                Console.WriteLine("   ❌ Connection failed!");
                return;
            }

            // Step 2: Authenticate Application
            Console.WriteLine("\n2️⃣ Authenticating application...");
            await _client.AuthenticateApplicationAsync();
            
            // Wait for authentication response
            await Task.Delay(2000);
            
            if (_client.IsApplicationAuthenticated)
            {
                Console.WriteLine("   ✅ Application authenticated!");
            }
            else
            {
                Console.WriteLine("   ❌ Application authentication failed!");
                return;
            }

            // Step 3: Authenticate Account (try demo account from API response)
            Console.WriteLine("\n3️⃣ Authenticating demo account 45291837...");
            await _client.AuthenticateAccountAsync(45291837);
            
            // Wait for authentication response
            await Task.Delay(2000);
            
            if (_client.IsAccountAuthenticated)
            {
                Console.WriteLine("   ✅ Account authenticated!");
            }
            else
            {
                Console.WriteLine("   ❌ Account authentication failed!");
                return;
            }

            // Step 4: Place a small test order (OPTIONAL - Commented out for safety)
            Console.WriteLine("\n4️⃣ Testing order placement capability...");
            Console.WriteLine("   ⚠️  Skipping actual order placement in test mode");
            Console.WriteLine("   ℹ️  To place a real test order, uncomment the code in TestCTraderConnection.cs");
            
            /*
            // UNCOMMENT THIS SECTION TO PLACE A REAL TEST ORDER
            var testSignal = new ParsedSignal
            {
                Asset = "EURUSD",
                Direction = TradeDirection.Buy,
                EntryPrice = null, // Market order
                StopLoss = null,
                TakeProfit = null,
                SignalType = SignalType.Text,
                ProviderChannelId = "TEST",
                ReceivedAt = DateTime.UtcNow
            };

            var result = await _orderManager.CreateOrderAsync(
                testSignal,
                CTraderOrderType.Market,
                isOpposite: false
            );

            if (result.Success)
            {
                Console.WriteLine("   ✅ ORDER PLACED SUCCESSFULLY!");
                Console.WriteLine($"   Order ID: {result.OrderId}");
                Console.WriteLine($"   Position ID: {result.PositionId}");
                Console.WriteLine($"   Entry Price: {result.ExecutedPrice}");
                Console.WriteLine($"   Volume: {result.ExecutedVolume} lots");
                
                // IMPORTANT: Close the test position immediately
                Console.WriteLine("\n   🔄 Closing test position...");
                if (result.PositionId.HasValue)
                {
                    var closed = await _orderManager.ClosePositionAsync(result.PositionId.Value, 0.01);
                    if (closed)
                    {
                        Console.WriteLine("   ✅ Test position closed!");
                    }
                }
            }
            else
            {
                Console.WriteLine($"   ❌ Order failed: {result.ErrorMessage}");
            }
            */

            // Step 5: Connection Status Summary
            Console.WriteLine("\n========================================");
            Console.WriteLine("  CONNECTION STATUS SUMMARY");
            Console.WriteLine("========================================");
            Console.WriteLine($"  Connected: {(_client.IsConnected ? "✅" : "❌")}");
            Console.WriteLine($"  App Authenticated: {(_client.IsApplicationAuthenticated ? "✅" : "❌")}");
            Console.WriteLine($"  Account Authenticated: {(_client.IsAccountAuthenticated ? "✅" : "❌")}");
            Console.WriteLine("========================================");

            // Step 6: Keep connection alive for main service
            Console.WriteLine("\n5️⃣ Keeping connection alive for main service...");
            Console.WriteLine("   ℹ️  Connection will remain active");
            Console.WriteLine("   ℹ️  Heartbeat is running in background");
            
            // Don't disconnect - let the main service use the connection
            // await _client.DisconnectAsync();

            Console.WriteLine("\n========================================");
            Console.WriteLine("  TEST COMPLETE! ✅");
            Console.WriteLine("========================================\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n❌ TEST FAILED: {ex.Message}");
            Console.WriteLine($"Stack Trace: {ex.StackTrace}");
            _logger.LogError(ex, "cTrader connection test failed");
            
            // Try to disconnect on error
            try
            {
                if (_client.IsConnected)
                {
                    await _client.DisconnectAsync();
                }
            }
            catch
            {
                // Ignore disconnect errors
            }
            
            throw; // Re-throw to prevent app from starting if test fails
        }
    }
}
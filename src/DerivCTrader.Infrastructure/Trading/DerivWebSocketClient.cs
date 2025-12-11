using System.Net.WebSockets;
using DerivCTrader.Infrastructure.Deriv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Websocket.Client;

namespace DerivCTrader.Infrastructure.Trading;

public class DerivWebSocketClient : IDerivClient, IDisposable
{
    private readonly ILogger<DerivWebSocketClient> _logger;
    private readonly string _wsUrl;
    private readonly string _apiToken;
    private WebsocketClient? _client;
    private bool _isConnected;
    private bool _isAuthorized;

    public bool IsConnected => _isConnected;
    public bool IsAuthorized => _isAuthorized;

    public DerivWebSocketClient(IConfiguration configuration, ILogger<DerivWebSocketClient> logger)
    {
        _logger = logger;
        
        var derivConfig = configuration.GetSection("Deriv");
        _wsUrl = derivConfig["WebSocketUrl"] ?? throw new InvalidOperationException("Deriv:WebSocketUrl not configured");
        _apiToken = derivConfig["Token"] ?? throw new InvalidOperationException("Deriv:Token not configured");
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_isConnected)
            return;

        try
        {
            _client = new WebsocketClient(new Uri(_wsUrl));
            
            _client.ReconnectTimeout = TimeSpan.FromSeconds(30);
            _client.ErrorReconnectTimeout = TimeSpan.FromSeconds(60);
            
            _client.ReconnectionHappened.Subscribe(info =>
            {
                _logger.LogInformation("Deriv WebSocket reconnection: {Type}", info.Type);
            });

            _client.DisconnectionHappened.Subscribe(info =>
            {
                _logger.LogWarning("Deriv WebSocket disconnected: {Type}", info.Type);
                _isConnected = false;
            });

            await _client.Start();
            
            // Authorize
            await AuthorizeAsync();
            
            _isConnected = true;
            _logger.LogInformation("Connected to Deriv WebSocket API");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Deriv WebSocket");
            throw;
        }
    }

    private async Task AuthorizeAsync()
    {
        var authRequest = new
        {
            authorize = _apiToken
        };

        var response = await SendRequestAsync(authRequest);
        
        if (response?["error"] != null)
        {
            throw new Exception($"Deriv authorization failed: {response["error"]}");
        }

        _isAuthorized = true;
        _logger.LogInformation("Deriv API authorized successfully");
    }

    public async Task<string?> ExecuteBinaryTradeAsync(
        string asset, 
        string direction, 
        decimal stake, 
        int expiryMinutes,
        string? timeframe = null,
        string? pattern = null)
    {
        try
        {
            if (!_isConnected)
            {
                await ConnectAsync();
            }

            // Convert asset to Deriv symbol format
            string derivSymbol = ConvertToDerivSymbol(asset);
            
            // Determine contract type (CALL/PUT based on direction)
            string contractType = direction.Equals("Call", StringComparison.OrdinalIgnoreCase) || 
                                 direction.Equals("Buy", StringComparison.OrdinalIgnoreCase)
                ? "CALL"
                : "PUT";

            // Calculate expiry timestamp
            var expiryTime = DateTimeOffset.UtcNow.AddMinutes(expiryMinutes).ToUnixTimeSeconds();

            var buyRequest = new
            {
                buy = 1,
                parameters = new
                {
                    amount = stake,
                    basis = "stake",
                    contract_type = contractType,
                    currency = "USD",
                    duration = expiryMinutes,
                    duration_unit = "m",
                    symbol = derivSymbol
                }
            };

            var response = await SendRequestAsync(buyRequest);

            if (response?["error"] != null)
            {
                _logger.LogError("Deriv trade execution failed: {Error}", response["error"]);
                return null;
            }

            var contractId = response?["buy"]?["contract_id"]?.ToString();
            
            _logger.LogInformation(
                "Deriv binary trade executed: {Asset} {Direction} Stake: ${Stake} Expiry: {Expiry}min ({ExpiryDisplay}) " +
                "Timeframe: {Timeframe} Pattern: {Pattern} ContractId: {ContractId}",
                asset, direction, stake, expiryMinutes, 
                expiryMinutes >= 60 ? $"{expiryMinutes/60.0:F1}H" : $"{expiryMinutes}M",
                timeframe ?? "N/A", pattern ?? "N/A", contractId);

            return contractId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing Deriv binary trade for {Asset}", asset);
            return null;
        }
    }

    private string ConvertToDerivSymbol(string asset)
    {
        // Handle volatility indices
        if (asset.StartsWith("VIX", StringComparison.OrdinalIgnoreCase) || 
            asset.Contains("Volatility", StringComparison.OrdinalIgnoreCase))
        {
            // Extract number (e.g., VIX25 -> 1HZ25V, Volatility 10 -> 1HZ10V)
            var match = System.Text.RegularExpressions.Regex.Match(asset, @"(\d+)");
            if (match.Success)
            {
                return $"1HZ{match.Value}V";  // Deriv format for volatility indices
            }
        }

        // Remove slashes from forex pairs (EUR/CAD -> EURCAD)
        string cleanedAsset = asset.Replace("/", "").Replace(" ", "");

        // Standard forex pairs - Deriv uses format like "frxEURUSD"
        // Check if it looks like a forex pair (6 or 7 characters, uppercase letters)
        if (cleanedAsset.Length >= 6 && cleanedAsset.Length <= 7 && 
            cleanedAsset.All(char.IsLetter))
        {
            // Add frx prefix if not already present
            if (!cleanedAsset.StartsWith("frx", StringComparison.OrdinalIgnoreCase))
            {
                return $"frx{cleanedAsset.ToUpper()}";
            }
            return cleanedAsset;
        }

        // Commodities like XAUUSD
        if (cleanedAsset.StartsWith("XAU", StringComparison.OrdinalIgnoreCase))
        {
            return "frxXAUUSD";
        }

        // Default: return cleaned asset
        return cleanedAsset;
    }

    private async Task<JObject?> SendRequestAsync(object request)
    {
        if (_client == null)
            throw new InvalidOperationException("WebSocket client not initialized");

        var requestJson = JsonConvert.SerializeObject(request);
        var tcs = new TaskCompletionSource<JObject?>();

        // Subscribe to messages
        var subscription = _client.MessageReceived.Subscribe(msg =>
        {
            try
            {
                var response = JObject.Parse(msg.Text ?? "{}");
                tcs.TrySetResult(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing Deriv response");
                tcs.TrySetException(ex);
            }
        });

        _client.Send(requestJson);

        // Wait for response with timeout
        var responseTask = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        
        subscription.Dispose();

        if (responseTask == tcs.Task)
        {
            return await tcs.Task;
        }
        
        throw new TimeoutException("Deriv API request timed out");
    }

    public Task<bool> IsConnectedAsync() => Task.FromResult(_isConnected);

    public async Task DisconnectAsync()
    {
        if (_client != null)
        {
            await _client.Stop(WebSocketCloseStatus.NormalClosure, "Closing connection");
            _isConnected = false;
            _logger.LogInformation("Disconnected from Deriv WebSocket");
        }
    }

    public async Task AuthorizeAsync(CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync();
    }

    public async Task<DerivTradeResult> PlaceBinaryOptionAsync(
        string asset,
        string direction,
        decimal stake,
        int durationMinutes,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var contractId = await ExecuteBinaryTradeAsync(asset, direction, stake, durationMinutes);
            return new DerivTradeResult
            {
                Success = !string.IsNullOrEmpty(contractId),
                ContractId = contractId,
                BuyPrice = stake
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to place binary option");
            return new DerivTradeResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<DerivContractOutcome> GetContractOutcomeAsync(
        string contractId,
        CancellationToken cancellationToken = default)
    {
        // TODO: Implement GetContractOutcomeAsync
        _logger.LogWarning("GetContractOutcomeAsync not yet fully implemented");
        return new DerivContractOutcome { IsWin = false, Profit = 0 };
    }

    public async Task<decimal> GetBalanceAsync(CancellationToken cancellationToken = default)
    {
        // TODO: Implement GetBalanceAsync
        _logger.LogWarning("GetBalanceAsync not yet fully implemented");
        return 0m;
    }

    public void Dispose()
    {
        _client?.Dispose();
    }
}

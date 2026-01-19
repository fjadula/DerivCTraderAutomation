using System.Net.WebSockets;
using DerivCTrader.Infrastructure.Deriv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Websocket.Client;

namespace DerivCTrader.Infrastructure.Trading;

public class DerivWebSocketClient : IDerivClient, DerivCTrader.Infrastructure.Deriv.IDerivTickProvider, IDisposable
{
    // ===== RESILIENCE CONFIGURATION =====
    private const int MAX_RECONNECT_ATTEMPTS = 5;
    private const int STABILITY_RESET_MINUTES = 30;
    private const int HEARTBEAT_INTERVAL_SECONDS = 30;

    // Reconnection tracking
    private int _reconnectAttempts = 0;
    private DateTime? _lastSuccessfulConnection;
    private readonly object _reconnectLock = new();

    // Heartbeat
    private Timer? _heartbeatTimer;
    private DateTime _lastHeartbeatResponse = DateTime.UtcNow;

    // Tick subscription support for price probe
    public event EventHandler<DerivCTrader.Infrastructure.Deriv.DerivTickEventArgs>? TickReceived;
    private readonly Dictionary<string, string> _activeTickSubscriptions = new(); // symbol -> subscriptionId
    private IDisposable? _tickMessageSubscription; // Persistent subscription for tick events

    public async Task<string> SubscribeTickAsync(string symbol)
    {
        if (!_isConnected)
        {
            await ConnectAsync();
        }
        var req = new { ticks = symbol, subscribe = 1 };
        var response = await SendRequestAsync(req);
        var subId = response?["tick" ]?["id"]?.ToString() ?? response?["subscription"]?["id"]?.ToString();
        if (!string.IsNullOrEmpty(subId))
        {
            _activeTickSubscriptions[symbol] = subId;
        }
        return subId ?? string.Empty;
    }

    public async Task UnsubscribeTickAsync(string subscriptionId)
    {
        var req = new { forget = subscriptionId };
        await SendRequestAsync(req);
        // Remove from active subscriptions
        var toRemove = _activeTickSubscriptions.Where(kv => kv.Value == subscriptionId).Select(kv => kv.Key).ToList();
        foreach (var key in toRemove)
            _activeTickSubscriptions.Remove(key);
    }
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
            // Check if we should reset reconnect counter (30 minutes of stability)
            CheckAndResetReconnectCounter();

            _client = new WebsocketClient(new Uri(_wsUrl));

            _client.ReconnectTimeout = TimeSpan.FromSeconds(30);
            _client.ErrorReconnectTimeout = TimeSpan.FromSeconds(60);

            _client.ReconnectionHappened.Subscribe(info =>
            {
                _logger.LogInformation("Deriv WebSocket reconnection: {Type}", info.Type);
                HandleReconnection(info.Type);
            });

            _client.DisconnectionHappened.Subscribe(info =>
            {
                _logger.LogWarning("Deriv WebSocket disconnected: {Type}", info.Type);
                _isConnected = false;
                _isAuthorized = false;
                StopHeartbeat();
            });

            await _client.Start();

            // Set up persistent tick message handler (separate from request/response flow)
            _tickMessageSubscription = _client.MessageReceived.Subscribe(msg =>
            {
                try
                {
                    var response = JObject.Parse(msg.Text ?? "{}");

                    // Handle ping/pong responses for heartbeat
                    if (response["ping"] != null || response["pong"] != null)
                    {
                        _lastHeartbeatResponse = DateTime.UtcNow;
                        return;
                    }

                    if (response["tick"] != null)
                    {
                        var tick = response["tick"];
                        var symbol = tick?["symbol"]?.ToString() ?? string.Empty;
                        var priceStr = tick?["quote"]?.ToString();
                        if (decimal.TryParse(priceStr, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var price))
                        {
                            _logger.LogDebug("[TICK] Received tick: Symbol={Symbol}, Price={Price}", symbol, price);
                            TickReceived?.Invoke(this, new DerivCTrader.Infrastructure.Deriv.DerivTickEventArgs { Symbol = symbol, Price = price });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing tick message");
                }
            });

            // Authorize
            await AuthorizeAsync();

            _isConnected = true;
            _lastSuccessfulConnection = DateTime.UtcNow;
            _reconnectAttempts = 0; // Reset on successful connection
            _logger.LogInformation("Connected to Deriv WebSocket API (reconnect counter reset)");

            // Start heartbeat to keep connection alive
            StartHeartbeat();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Deriv WebSocket");
            HandleConnectionFailure();
            throw;
        }
    }

    /// <summary>
    /// Handle reconnection events - track attempts and trigger restart if needed
    /// </summary>
    private void HandleReconnection(ReconnectionType type)
    {
        if (type == ReconnectionType.Error || type == ReconnectionType.Lost)
        {
            lock (_reconnectLock)
            {
                _reconnectAttempts++;
                _logger.LogWarning("Deriv reconnection attempt {Attempt}/{Max}", _reconnectAttempts, MAX_RECONNECT_ATTEMPTS);

                if (_reconnectAttempts >= MAX_RECONNECT_ATTEMPTS)
                {
                    _logger.LogCritical("Max reconnection attempts ({Max}) reached. Triggering service restart...", MAX_RECONNECT_ATTEMPTS);
                    TriggerServiceRestart();
                }
            }
        }
        else if (type == ReconnectionType.Initial)
        {
            // Successful reconnection - reset counter
            _reconnectAttempts = 0;
            _lastSuccessfulConnection = DateTime.UtcNow;
            _logger.LogInformation("Deriv WebSocket reconnected successfully, counter reset");
        }
    }

    /// <summary>
    /// Handle connection failure - increment counter and check for restart
    /// </summary>
    private void HandleConnectionFailure()
    {
        lock (_reconnectLock)
        {
            _reconnectAttempts++;
            _logger.LogError("Deriv connection failed. Attempt {Attempt}/{Max}", _reconnectAttempts, MAX_RECONNECT_ATTEMPTS);

            if (_reconnectAttempts >= MAX_RECONNECT_ATTEMPTS)
            {
                _logger.LogCritical("Max connection attempts ({Max}) reached. Triggering service restart...", MAX_RECONNECT_ATTEMPTS);
                TriggerServiceRestart();
            }
        }
    }

    /// <summary>
    /// Check if 30 minutes of stability has passed and reset counter
    /// </summary>
    private void CheckAndResetReconnectCounter()
    {
        if (_lastSuccessfulConnection.HasValue)
        {
            var timeSinceLastSuccess = DateTime.UtcNow - _lastSuccessfulConnection.Value;
            if (timeSinceLastSuccess.TotalMinutes >= STABILITY_RESET_MINUTES && _reconnectAttempts > 0)
            {
                _logger.LogInformation("Connection stable for {Minutes} minutes. Resetting reconnect counter from {Count} to 0",
                    STABILITY_RESET_MINUTES, _reconnectAttempts);
                _reconnectAttempts = 0;
            }
        }
    }

    /// <summary>
    /// Start heartbeat timer to keep connection alive
    /// </summary>
    private void StartHeartbeat()
    {
        StopHeartbeat(); // Ensure no duplicate timers

        _heartbeatTimer = new Timer(async _ =>
        {
            try
            {
                if (_client != null && _isConnected)
                {
                    var pingRequest = new { ping = 1 };
                    _client.Send(JsonConvert.SerializeObject(pingRequest));
                    _logger.LogDebug("Deriv heartbeat ping sent");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat ping failed");
            }
        }, null, TimeSpan.FromSeconds(HEARTBEAT_INTERVAL_SECONDS), TimeSpan.FromSeconds(HEARTBEAT_INTERVAL_SECONDS));

        _logger.LogInformation("Deriv heartbeat started (every {Seconds} seconds)", HEARTBEAT_INTERVAL_SECONDS);
    }

    /// <summary>
    /// Stop heartbeat timer
    /// </summary>
    private void StopHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    /// <summary>
    /// Trigger service restart via Environment.Exit
    /// </summary>
    private void TriggerServiceRestart()
    {
        _logger.LogCritical("===== DERIV CONNECTION FAILURE - INITIATING SERVICE RESTART =====");

        // Give logs time to flush
        Task.Delay(1000).Wait();

        // Exit with code 1 to signal restart needed
        Environment.Exit(1);
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

        // Subscribe to messages (tick events are handled by persistent _tickMessageSubscription)
        var subscription = _client.MessageReceived.Subscribe(msg =>
        {
            try
            {
                var response = JObject.Parse(msg.Text ?? "{}");
                // Note: tick events are now handled by persistent subscription in ConnectAsync
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
            StopHeartbeat();
            _tickMessageSubscription?.Dispose();
            _tickMessageSubscription = null;
            await _client.Stop(WebSocketCloseStatus.NormalClosure, "Closing connection");
            _isConnected = false;
            _isAuthorized = false;
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

    public async Task<decimal?> GetSpotPriceAsync(string symbol, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_isConnected)
            {
                await ConnectAsync(cancellationToken);
            }

            var derivSymbol = ConvertToDerivSymbol(symbol);

            var request = new
            {
                ticks_history = derivSymbol,
                end = "latest",
                count = 1,
                style = "ticks"
            };

            var response = await SendRequestAsync(request);

            if (response?["error"] != null)
            {
                _logger.LogWarning("Failed to get spot price for {Symbol}: {Error}",
                    symbol, response["error"]);
                return null;
            }

            var prices = response?["history"]?["prices"]?.ToObject<decimal[]>();
            if (prices != null && prices.Length > 0)
            {
                return prices[0];
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting spot price for {Symbol}", symbol);
            return null;
        }
    }

    public async Task<decimal?> GetHistoricalPriceAsync(string symbol, DateTime timestamp, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_isConnected)
            {
                await ConnectAsync(cancellationToken);
            }

            var derivSymbol = ConvertToDerivSymbol(symbol);
            var epochTime = ((DateTimeOffset)timestamp).ToUnixTimeSeconds();

            var request = new
            {
                ticks_history = derivSymbol,
                end = epochTime,
                count = 1,
                style = "ticks"
            };

            var response = await SendRequestAsync(request);

            if (response?["error"] != null)
            {
                _logger.LogWarning("Failed to get historical price for {Symbol} at {Timestamp}: {Error}",
                    symbol, timestamp, response["error"]);
                return null;
            }

            var prices = response?["history"]?["prices"]?.ToObject<decimal[]>();
            if (prices != null && prices.Length > 0)
            {
                return prices[0];
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting historical price for {Symbol} at {Timestamp}", symbol, timestamp);
            return null;
        }
    }

    public void Dispose()
    {
        StopHeartbeat();
        _tickMessageSubscription?.Dispose();
        _client?.Dispose();
    }
}

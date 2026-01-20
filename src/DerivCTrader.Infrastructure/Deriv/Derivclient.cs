using System.Net.WebSockets;
using System.Text;
using DerivCTrader.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DerivCTrader.Infrastructure.Deriv;

public class DerivClient : IDerivClient, IDisposable
{
    private readonly ILogger<DerivClient> _logger;
    private readonly DerivConfig _config;
    private ClientWebSocket? _webSocket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private int _requestId = 1;

    public bool IsConnected { get; private set; }
    public bool IsAuthorized { get; private set; }

    public DerivClient(ILogger<DerivClient> logger, DerivConfig config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Clean up any existing connection
            await CleanupConnectionAsync();

            _logger.LogInformation("Connecting to Deriv at {Url}...", _config.WebSocketUrl);
            Console.WriteLine($"=== DERIV: Connecting to {_config.WebSocketUrl} ===");

            _webSocket = new ClientWebSocket();
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            
            await _webSocket.ConnectAsync(new Uri(_config.WebSocketUrl), cancellationToken);

            IsConnected = true;
            _logger.LogInformation("✅ Connected to Deriv");
            Console.WriteLine("=== DERIV: ✅ Connected ===");

            // No background receive task - we use request/response pattern
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Deriv");
            Console.WriteLine($"=== DERIV: ❌ Connection failed: {ex.Message} ===");
            IsConnected = false;
            await CleanupConnectionAsync();
            throw;
        }
    }

    private async Task CleanupConnectionAsync()
    {
        try
        {
            if (_webSocket != null)
            {
                if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.CloseReceived)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Cleanup", CancellationToken.None);
                }
                _webSocket.Dispose();
                _webSocket = null;
            }

            IsConnected = false;
            IsAuthorized = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during connection cleanup");
        }
    }

    public async Task AuthorizeAsync(CancellationToken cancellationToken = default)
    {
        // Prevent concurrent authorization attempts from multiple services
        await _authLock.WaitAsync(cancellationToken);
        try
        {
            // If already authorized, skip
            if (IsAuthorized)
            {
                _logger.LogInformation("Already authorized with Deriv");
                return;
            }

            _logger.LogInformation("Authorizing with Deriv...");
            Console.WriteLine("=== DERIV: Authorizing ===");

            // Check connection state and reconnect if needed
            if (!IsConnected || _webSocket == null || _webSocket.State != WebSocketState.Open)
            {
                _logger.LogWarning("Deriv connection not ready (State: {State}), reconnecting...", 
                    _webSocket?.State.ToString() ?? "null");
                await ConnectAsync(cancellationToken);
            }

            var request = new
            {
                authorize = _config.Token,
                req_id = _requestId++
            };

            var response = await SendAndReceiveAsync(request, cancellationToken);

            if (response?["error"] != null)
            {
                var error = response?["error"];
                throw new Exception($"Authorization failed: {error?["message"] ?? "Unknown error"}");
            }

            IsAuthorized = true;
            var balance = response?["authorize"]?["balance"]?.ToString() ?? "unknown";

            _logger.LogInformation("✅ Authorized with Deriv. Balance: {Balance}", balance);
            Console.WriteLine($"=== DERIV: ✅ Authorized. Balance: {balance} ===");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authorization failed");
            Console.WriteLine($"=== DERIV: ❌ Auth failed: {ex.Message} ===");
            IsAuthorized = false;
            throw;
        }
        finally
        {
            _authLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            _logger.LogInformation("Disconnecting from Deriv...");
            await CleanupConnectionAsync();
            _logger.LogInformation("✅ Disconnected from Deriv");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during disconnect");
        }
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
            var derivSymbol = DerivAssetMapper.ToDerivSymbol(asset);
            var contractType = direction.ToUpper() == "BUY" || direction.ToUpper() == "CALL" ? "CALL" : "PUT";

            _logger.LogInformation("Placing binary option: {Symbol} {Type} {Stake} USD {Duration}min",
                derivSymbol, contractType, stake, durationMinutes);
            Console.WriteLine($"=== DERIV ORDER: {derivSymbol} {contractType} ${stake} {durationMinutes}min ===");

            if (!IsAuthorized)
                throw new InvalidOperationException("Not authorized with Deriv");

            // Step 1: Get proposal (price quote)
            // Note: Deriv API expects lowercase symbols for forex pairs (eur/usd),
            // but uppercase for volatility indices (R_10, 1HZ100V)
            var symbolForApi = derivSymbol.Contains("/")
                ? derivSymbol.ToLowerInvariant()  // Forex: EUR/USD -> eur/usd
                : derivSymbol;  // Keep volatility indices as-is: R_10, 1HZ100V

            var proposalReq = new
            {
                proposal = 1,
                amount = stake,
                basis = "stake",
                contract_type = contractType,
                currency = "USD",
                duration = durationMinutes,
                duration_unit = "m",
                symbol = symbolForApi,
                req_id = _requestId++
            };

            var proposalRes = await SendAndReceiveAsync(proposalReq, cancellationToken);

            if (proposalRes["error"] != null)
            {
                var error = proposalRes?["error"];
                var errorMsg = error?["message"]?.ToString();
                var errorCode = error?["code"]?.ToString();
                var errorDetails = error?["details"]?.ToString();

                _logger.LogError("Proposal failed for symbol {Symbol}: Code={Code}, Message={Message}, Details={Details}, FullError={FullError}",
                    derivSymbol, errorCode, errorMsg, errorDetails, error?.ToString());

                return new DerivTradeResult
                {
                    Success = false,
                    ErrorMessage = errorMsg ?? errorDetails ?? "Unknown error",
                    ErrorCode = errorCode
                };
            }

            var proposalId = proposalRes["proposal"]?["id"]?.ToString();
            var askPrice = proposalRes["proposal"]?["ask_price"]?.ToObject<decimal>();
            var payout = proposalRes["proposal"]?["payout"]?.ToObject<decimal>();

            if (string.IsNullOrEmpty(proposalId))
            {
                return new DerivTradeResult
                {
                    Success = false,
                    ErrorMessage = "No proposal ID received"
                };
            }

            _logger.LogInformation("Proposal received: ID={ProposalId}, Price={Price}, Payout={Payout}",
                proposalId, askPrice, payout);

            // Step 2: Buy the contract
            var buyReq = new
            {
                buy = proposalId,
                price = askPrice,
                req_id = _requestId++
            };

            var buyRes = await SendAndReceiveAsync(buyReq, cancellationToken);

            if (buyRes?["error"] != null)
            {
                var error = buyRes?["error"];
                _logger.LogError("Buy failed: {Message}", error?["message"] ?? "Unknown error");
                return new DerivTradeResult
                {
                    Success = false,
                    ErrorMessage = error?["message"]?.ToString(),
                    ErrorCode = error?["code"]?.ToString()
                };
            }

            var contractId = buyRes?["buy"]?["contract_id"]?.ToString() ?? "unknown";
            var purchasePrice = buyRes?["buy"]?["buy_price"]?.ToObject<decimal>() ?? 0;
            var actualPayout = buyRes?["buy"]?["payout"]?.ToObject<decimal>();

            _logger.LogInformation("✅ Binary option purchased: Contract={ContractId}, Price={Price}",
                contractId, purchasePrice);
            Console.WriteLine($"=== DERIV: ✅ Order executed ===");
            Console.WriteLine($"   Contract ID: {contractId}");
            Console.WriteLine($"   Purchase Price: ${purchasePrice}");
            Console.WriteLine($"   Potential Payout: ${actualPayout}");

            return new DerivTradeResult
            {
                Success = true,
                ContractId = contractId,
                PurchasePrice = purchasePrice,
                Payout = actualPayout,
                PurchaseTime = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error placing binary option");
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
        try
        {
            var request = new
            {
                proposal_open_contract = 1,
                contract_id = contractId,
                req_id = _requestId++
            };

            var response = await SendAndReceiveAsync(request, cancellationToken);

            if (response?["error"] != null)
            {
                throw new Exception($"Failed to get contract: {response?["error"]?["message"] ?? "Unknown error"}");
            }

            var contract = response?["proposal_open_contract"];
            var status = contract?["status"]?.ToString();
            var profit = contract?["profit"]?.ToObject<decimal>() ?? 0;
            var exitSpot = contract?["exit_tick"]?.ToObject<decimal>();

            var outcome = status?.ToLower() == "won" ? "Win" : "Loss";

            return new DerivContractOutcome
            {
                ContractId = contractId,
                Status = outcome,
                Profit = profit,
                ExitSpot = exitSpot,
                SettledAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting contract outcome");
            throw;
        }
    }

    public async Task<decimal> GetBalanceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new
            {
                balance = 1,
                req_id = _requestId++
            };

            var response = await SendAndReceiveAsync(request, cancellationToken);

            if (response?["error"] != null)
            {
                throw new Exception($"Failed to get balance: {response?["error"]?["message"] ?? "Unknown error"}");
            }

            var balance = response?["balance"]?["balance"]?.ToObject<decimal>() ?? 0;
            return balance;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting balance");
            throw;
        }
    }

    /// <summary>
    /// Get current spot price for a symbol using tick history API.
    /// Returns the most recent tick price (single snapshot, not streaming).
    /// </summary>
    public async Task<decimal?> GetSpotPriceAsync(string symbol, CancellationToken cancellationToken = default)
    {
        try
        {
            // Convert to Deriv symbol format (e.g., USDJPY -> frxUSDJPY)
            var derivSymbol = DerivAssetMapper.ToDerivSymbol(symbol);

            _logger.LogDebug("Fetching spot price for {Symbol} ({DerivSymbol})", symbol, derivSymbol);

            // Use ticks_history with count=1 to get single most recent tick
            // This doesn't require subscription and returns immediately
            var request = new
            {
                ticks_history = derivSymbol,
                end = "latest",
                count = 1,
                style = "ticks",
                req_id = _requestId++
            };

            var response = await SendAndReceiveAsync(request, cancellationToken);

            if (response?["error"] != null)
            {
                var error = response?["error"];
                _logger.LogWarning("Failed to get spot price for {Symbol}: {Error}",
                    symbol, error?["message"] ?? "Unknown error");
                return null;
            }

            // Extract the price from history response
            // Response format: { "history": { "prices": [123.456], "times": [1234567890] } }
            var prices = response?["history"]?["prices"];
            if (prices == null || !prices.HasValues)
            {
                _logger.LogWarning("No price data returned for {Symbol}", symbol);
                return null;
            }

            var price = prices.First?.ToObject<decimal>();

            _logger.LogDebug("Got spot price for {Symbol}: {Price}", symbol, price);
            return price;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting spot price for {Symbol}", symbol);
            return null;
        }
    }

    /// <summary>
    /// Get historical spot price for a symbol at a specific time.
    /// Uses ticks_history with a specific epoch timestamp.
    /// </summary>
    public async Task<decimal?> GetHistoricalPriceAsync(string symbol, DateTime timestamp, CancellationToken cancellationToken = default)
    {
        try
        {
            var derivSymbol = DerivAssetMapper.ToDerivSymbol(symbol);
            var epochTime = ((DateTimeOffset)timestamp).ToUnixTimeSeconds();

            _logger.LogDebug("Fetching historical price for {Symbol} at {Timestamp} (epoch: {Epoch})",
                symbol, timestamp, epochTime);

            // Use ticks_history with end=epoch to get tick at or before that time
            var request = new
            {
                ticks_history = derivSymbol,
                end = epochTime,
                count = 1,
                style = "ticks",
                req_id = _requestId++
            };

            var response = await SendAndReceiveAsync(request, cancellationToken);

            if (response?["error"] != null)
            {
                var error = response?["error"];
                _logger.LogWarning("Failed to get historical price for {Symbol}: {Error}",
                    symbol, error?["message"] ?? "Unknown error");
                return null;
            }

            var prices = response?["history"]?["prices"];
            if (prices == null || !prices.HasValues)
            {
                _logger.LogWarning("No historical price data returned for {Symbol} at {Timestamp}", symbol, timestamp);
                return null;
            }

            var price = prices.First?.ToObject<decimal>();

            _logger.LogDebug("Got historical price for {Symbol} at {Timestamp}: {Price}", symbol, timestamp, price);
            return price;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting historical price for {Symbol} at {Timestamp}", symbol, timestamp);
            return null;
        }
    }

    private async Task<JObject> SendAndReceiveAsync(object request, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            // Auto-reconnect if WebSocket is not in Open state
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            {
                _logger.LogWarning("WebSocket not ready (State: {State}). Attempting reconnection...",
                    _webSocket?.State.ToString() ?? "null");

                // Try to reconnect (max 3 attempts)
                var reconnected = false;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        _logger.LogInformation("Reconnection attempt {Attempt}/3...", attempt);
                        await ConnectAsync(cancellationToken);

                        // Re-authorize if we have credentials
                        if (!string.IsNullOrEmpty(_config.Token))
                        {
                            await AuthorizeAsync(cancellationToken);
                        }

                        reconnected = true;
                        _logger.LogInformation("Reconnection successful");
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Reconnection attempt {Attempt}/3 failed", attempt);
                        if (attempt < 3)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
                        }
                    }
                }

                if (!reconnected)
                {
                    throw new InvalidOperationException(
                        "Failed to reconnect after 3 attempts. WebSocket remains unavailable.");
                }
            }

            var json = JsonConvert.SerializeObject(request);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                cancellationToken);

            _logger.LogDebug("Sent: {Json}", json);

            // Receive response with timeout
            var buffer = new byte[8192];
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            
            var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), linkedCts.Token);

            var responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
            _logger.LogDebug("Received: {Json}", responseJson);

            return JObject.Parse(responseJson);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        _webSocket?.Dispose();
        _sendLock?.Dispose();
        _authLock?.Dispose();
    }
}
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
            var proposalReq = new
            {
                proposal = 1,
                amount = stake,
                basis = "stake",
                contract_type = contractType,
                currency = "USD",
                duration = durationMinutes,
                duration_unit = "m",
                symbol = derivSymbol,
                req_id = _requestId++
            };

            var proposalRes = await SendAndReceiveAsync(proposalReq, cancellationToken);

            if (proposalRes["error"] != null)
            {
                var error = proposalRes?["error"];
                _logger.LogError("Proposal failed: {Message}", error?["message"] ?? "Unknown error");
                return new DerivTradeResult
                {
                    Success = false,
                    ErrorMessage = error?["message"]?.ToString(),
                    ErrorCode = error?["code"]?.ToString()
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

    private async Task<JObject> SendAndReceiveAsync(object request, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            // Verify WebSocket is in valid state
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException(
                    $"WebSocket is not ready for communication. State: {_webSocket?.State.ToString() ?? "null"}");
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
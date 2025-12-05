using System.Net.WebSockets;
using System.Text;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Infrastructure.Deriv.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DerivCTrader.Infrastructure.Deriv;

public class DerivClient : IDerivClient, IDisposable
{
    private readonly ILogger<DerivClient> _logger;
    private readonly DerivConfig _config;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
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
            _logger.LogInformation("Connecting to Deriv at {Url}...", _config.WebSocketUrl);
            Console.WriteLine($"=== DERIV: Connecting to {_config.WebSocketUrl} ===");

            _webSocket = new ClientWebSocket();
            await _webSocket.ConnectAsync(new Uri(_config.WebSocketUrl), cancellationToken);

            IsConnected = true;
            _logger.LogInformation("✅ Connected to Deriv");
            Console.WriteLine("=== DERIV: ✅ Connected ===");

            // Start receiving messages
            _receiveCts = new CancellationTokenSource();
            _receiveTask = Task.Run(() => ReceiveMessagesAsync(_receiveCts.Token), _receiveCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Deriv");
            Console.WriteLine($"=== DERIV: ❌ Connection failed: {ex.Message} ===");
            IsConnected = false;
            throw;
        }
    }

    public async Task AuthorizeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Authorizing with Deriv...");
            Console.WriteLine("=== DERIV: Authorizing ===");

            if (!IsConnected)
                throw new InvalidOperationException("Not connected to Deriv");

            var request = new
            {
                authorize = _config.Token,
                req_id = _requestId++
            };

            var response = await SendAndReceiveAsync(request, cancellationToken);

            if (response["error"] != null)
            {
                var error = response["error"];
                throw new Exception($"Authorization failed: {error["message"]}");
            }

            IsAuthorized = true;
            var balance = response["authorize"]?["balance"]?.ToString() ?? "unknown";

            _logger.LogInformation("✅ Authorized with Deriv. Balance: {Balance}", balance);
            Console.WriteLine($"=== DERIV: ✅ Authorized. Balance: {balance} ===");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authorization failed");
            Console.WriteLine($"=== DERIV: ❌ Auth failed: {ex.Message} ===");
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            _logger.LogInformation("Disconnecting from Deriv...");

            _receiveCts?.Cancel();

            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnect", CancellationToken.None);
            }

            IsConnected = false;
            IsAuthorized = false;

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
                var error = proposalRes["error"];
                _logger.LogError("Proposal failed: {Message}", error["message"]);
                return new DerivTradeResult
                {
                    Success = false,
                    ErrorMessage = error["message"]?.ToString(),
                    ErrorCode = error["code"]?.ToString()
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

            if (buyRes["error"] != null)
            {
                var error = buyRes["error"];
                _logger.LogError("Buy failed: {Message}", error["message"]);
                return new DerivTradeResult
                {
                    Success = false,
                    ErrorMessage = error["message"]?.ToString(),
                    ErrorCode = error["code"]?.ToString()
                };
            }

            var contractId = buyRes["buy"]?["contract_id"]?.ToString();
            var purchasePrice = buyRes["buy"]?["buy_price"]?.ToObject<decimal>() ?? 0;
            var actualPayout = buyRes["buy"]?["payout"]?.ToObject<decimal>();

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

            if (response["error"] != null)
            {
                throw new Exception($"Failed to get contract: {response["error"]["message"]}");
            }

            var contract = response["proposal_open_contract"];
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

            if (response["error"] != null)
            {
                throw new Exception($"Failed to get balance: {response["error"]["message"]}");
            }

            var balance = response["balance"]?["balance"]?.ToObject<decimal>() ?? 0;
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
            var json = JsonConvert.SerializeObject(request);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _webSocket!.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                cancellationToken);

            _logger.LogDebug("Sent: {Json}", json);

            // Receive response
            var buffer = new byte[8192];
            var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            var responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
            _logger.LogDebug("Received: {Json}", responseJson);

            return JObject.Parse(responseJson);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];

        try
        {
            while (!cancellationToken.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogWarning("WebSocket closed by server");
                    await DisconnectAsync();
                    break;
                }

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                _logger.LogDebug("Background received: {Json}", json);

                // Handle subscription updates here if needed
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Receive task cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error receiving messages");
            await DisconnectAsync();
        }
    }

    public void Dispose()
    {
        _receiveCts?.Cancel();
        _receiveCts?.Dispose();
        _webSocket?.Dispose();
        _sendLock?.Dispose();
    }
}
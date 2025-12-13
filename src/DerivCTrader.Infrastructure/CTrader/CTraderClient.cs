using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DerivCTrader.Infrastructure.CTrader.Interfaces;
using DerivCTrader.Infrastructure.CTrader.Models;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OpenAPI.Net;
using OpenAPI.Net.Helpers;

using PayloadType = DerivCTrader.Infrastructure.CTrader.Models.ProtoOAPayloadType;

namespace DerivCTrader.Infrastructure.CTrader;

/// <summary>
/// cTrader TCP client for port 5035 (native protobuf protocol)
/// </summary>
public class CTraderClient : ICTraderClient, IDisposable
{
    private readonly ILogger<CTraderClient> _logger;
    private readonly IConfiguration _configuration;
    private TcpClient? _tcpClient;
    private Stream? _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<byte[]>> _pendingResponses = new();
    private readonly Dictionary<(int PayloadType, string ClientMsgId), TaskCompletionSource<byte[]>> _pendingResponsesByClientMsgId = new();
    private readonly object _pendingResponsesLock = new();
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    
    private string _clientId = string.Empty;
    private string _clientSecret = string.Empty;
    private string _accessToken = string.Empty;
    private string _refreshToken = string.Empty;
    private long _accountId;
    private string _host = string.Empty;
    private int _port;
    private bool _useSsl;
    private int _heartbeatInterval;

    public bool IsConnected => _tcpClient?.Connected ?? false;
    public bool IsApplicationAuthenticated { get; private set; }
    public bool IsAccountAuthenticated { get; private set; }
    public long AccountId => _accountId;

    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler<CTraderMessage>? MessageReceived;

    public CTraderClient(ILogger<CTraderClient> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        
        LoadConfiguration();
    }

    private void LoadConfiguration()
    {
        var ctraderSection = _configuration.GetSection("CTrader");
        _clientId = ctraderSection["ClientId"] ?? throw new InvalidOperationException("CTrader:ClientId is required");
        _clientSecret = ctraderSection["ClientSecret"] ?? throw new InvalidOperationException("CTrader:ClientSecret is required");
        _accessToken = ctraderSection["AccessToken"] ?? throw new InvalidOperationException("CTrader:AccessToken is required");
        _refreshToken = ctraderSection["RefreshToken"] ?? string.Empty;
        
        var environment = ctraderSection["Environment"] ?? "Demo";
        var accountIdKey = environment == "Live" ? "LiveAccountId" : "DemoAccountId";
        _accountId = long.Parse(ctraderSection[accountIdKey] ?? throw new InvalidOperationException($"CTrader:{accountIdKey} is required"));
        
        // NEW: TCP configuration instead of WebSocket
        _host = ctraderSection["Host"] ?? "demo.ctraderapi.com";
        _port = int.Parse(ctraderSection["Port"] ?? "5035");
        _useSsl = bool.Parse(ctraderSection["UseSsl"] ?? "true");
        _heartbeatInterval = int.Parse(ctraderSection["HeartbeatIntervalSeconds"] ?? "25");

        _logger.LogInformation("CTrader configuration loaded: Environment={Environment}, Host={Host}:{Port}, SSL={SSL}", 
            environment, _host, _port, _useSsl);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("=== CTRADER: Connecting to {Host}:{Port} (TCP/SSL) ===", _host, _port);
            Console.WriteLine($"=== CTRADER: Connecting to {_host}:{_port} (TCP/SSL) ===");

            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(_host, _port, cancellationToken);

            // Wrap in SSL stream if required
            if (_useSsl)
            {
                var sslStream = new SslStream(
                    _tcpClient.GetStream(),
                    false,
                    ValidateServerCertificate,
                    null);

                await sslStream.AuthenticateAsClientAsync(_host);
                _stream = sslStream;
                
                _logger.LogInformation("=== CTRADER: ✅ SSL handshake complete ===");
            }
            else
            {
                _stream = _tcpClient.GetStream();
            }

            _logger.LogInformation("=== CTRADER: ✅ Connected ===");
            Console.WriteLine("=== CTRADER: ✅ Connected ===");

            // Start receive loop
            _receiveCts = new CancellationTokenSource();
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), _receiveCts.Token);

            ConnectionStateChanged?.Invoke(this, true);

            // Start heartbeat
            _ = Task.Run(() => HeartbeatLoopAsync(_receiveCts.Token), _receiveCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to cTrader");
            Console.WriteLine($"=== CTRADER: ❌ Connection failed: {ex.Message} ===");
            throw;
        }
    }

    private bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        // For production, implement proper certificate validation
        // For now, accept all certificates
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        _logger.LogWarning("Certificate validation warning: {Errors}", sslPolicyErrors);
        return true; // Accept anyway for demo
    }

    public async Task DisconnectAsync()
    {
        try
        {
            _receiveCts?.Cancel();
            
            _stream?.Close();
            _tcpClient?.Close();

            IsApplicationAuthenticated = false;
            IsAccountAuthenticated = false;
            ConnectionStateChanged?.Invoke(this, false);

            _logger.LogInformation("Disconnected from cTrader");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during disconnect");
        }
    }

    public async Task AuthenticateApplicationAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("=== CTRADER: Authenticating application ===");
            Console.WriteLine("=== CTRADER: Authenticating application ===");

            var authReq = new ProtoOAApplicationAuthReq
            {
                ClientId = _clientId,
                ClientSecret = _clientSecret
            };

            await SendMessageAsync(authReq, (int)PayloadType.ProtoOaApplicationAuthReq, cancellationToken);
            
            // Wait for response
            var response = await WaitForResponseAsync<ProtoOAApplicationAuthRes>(
                (int)PayloadType.ProtoOaApplicationAuthRes,
                TimeSpan.FromSeconds(10),
                cancellationToken);

            if (response != null)
            {
                IsApplicationAuthenticated = true;
                _logger.LogInformation("=== CTRADER: ✅ Application authenticated ===");
                Console.WriteLine("=== CTRADER: ✅ Application authenticated ===");
            }
            else
            {
                throw new Exception("Application authentication failed - no response");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Application authentication failed");
            Console.WriteLine($"=== CTRADER: ❌ Application authentication failed: {ex.Message} ===");
            throw;
        }
    }

    public async Task<List<ProtoOACtidTraderAccount>> GetAccountListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("=== CTRADER: Fetching account list ===");
            Console.WriteLine("=== CTRADER: Fetching account list ===");

            var req = new ProtoOAGetAccountListByAccessTokenReq
            {
                AccessToken = _accessToken
            };

            await SendMessageAsync(req, (int)PayloadType.ProtoOaGetAccountListByAccessTokenReq, cancellationToken);

            var response = await WaitForResponseAsync<ProtoOAGetAccountListByAccessTokenRes>(
                (int)PayloadType.ProtoOaGetAccountListByAccessTokenRes,
                TimeSpan.FromSeconds(10),
                cancellationToken);

            if (response != null)
            {
                _logger.LogInformation("=== CTRADER: ✅ Found {Count} accounts ===", response.CtidTraderAccount.Count);
                Console.WriteLine($"=== CTRADER: ✅ Found {response.CtidTraderAccount.Count} accounts ===");
                
                foreach (var account in response.CtidTraderAccount)
                {
                    var accountType = account.IsLive ? "LIVE" : "DEMO";
                    _logger.LogInformation("  Account ID: {AccountId} ({Type})", account.CtidTraderAccountId, accountType);
                    Console.WriteLine($"  Account ID: {account.CtidTraderAccountId} ({accountType})");
                }
                
                return response.CtidTraderAccount.ToList();
            }
            else
            {
                throw new Exception("Failed to get account list - no response");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get account list");
            Console.WriteLine($"=== CTRADER: ❌ Failed to get account list: {ex.Message} ===");
            throw;
        }
    }

    public async Task AuthenticateAccountAsync(long accountId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("=== CTRADER: Authenticating account {AccountId} ===", accountId);
            Console.WriteLine($"=== CTRADER: Authenticating account {accountId} ===");

            var authReq = new ProtoOAAccountAuthReq
            {
                CtidTraderAccountId = accountId,
                AccessToken = _accessToken
            };

            await SendMessageAsync(authReq, (int)PayloadType.ProtoOaAccountAuthReq, cancellationToken);

            // Wait for response
            var response = await WaitForResponseAsync<ProtoOAAccountAuthRes>(
                (int)PayloadType.ProtoOaAccountAuthRes,
                TimeSpan.FromSeconds(10),
                cancellationToken);

            if (response != null)
            {
                IsAccountAuthenticated = true;
                _logger.LogInformation("=== CTRADER: ✅ Account {AccountId} authenticated ===", accountId);
                Console.WriteLine($"=== CTRADER: ✅ Account {accountId} authenticated ===");
            }
            else
            {
                throw new Exception("Account authentication failed - no response");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Account authentication failed");
            Console.WriteLine($"=== CTRADER: ❌ Account authentication failed: {ex.Message} ===");
            throw;
        }
    }

    public async Task<bool> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected || !IsAccountAuthenticated)
        {
            _logger.LogWarning("Reconcile skipped: not connected/account-authenticated");
            return false;
        }

        try
        {
            _logger.LogInformation("=== CTRADER: Reconciling account stream (AccountId={AccountId}) ===", _accountId);

            var req = new ProtoOAReconcileReq
            {
                CtidTraderAccountId = _accountId
            };

            await SendMessageAsync(req, (int)PayloadType.ProtoOaReconcileReq, cancellationToken);

            var res = await WaitForResponseAsync<ProtoOAReconcileRes>(
                (int)PayloadType.ProtoOaReconcileRes,
                TimeSpan.FromSeconds(10),
                cancellationToken);

            if (res == null)
            {
                _logger.LogWarning("Reconcile timed out or returned null (AccountId={AccountId})", _accountId);
                return false;
            }

            _logger.LogInformation("=== CTRADER: ✅ Reconcile complete ===");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reconcile failed");
            return false;
        }
    }

    public async Task SendMessageAsync<T>(T message, int payloadType, CancellationToken cancellationToken = default)
    {
        // Preserve legacy behavior while always attaching a ClientMsgId for correlation.
        // Callers that don't care can ignore the id.
        _ = await SendMessageWithClientMsgIdAsync(message, payloadType, cancellationToken);
    }

    public async Task<string> SendMessageWithClientMsgIdAsync<T>(T message, int payloadType, CancellationToken cancellationToken = default)
    {
        if (_stream == null || !IsConnected)
            throw new InvalidOperationException("Not connected to cTrader");

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            byte[] innerPayload;
            
            if (message is IMessage protoMessage)
            {
                innerPayload = protoMessage.ToByteArray();
            }
            else
            {
                var json = JsonConvert.SerializeObject(message);
                innerPayload = Encoding.UTF8.GetBytes(json);
            }

            // Wrap in ProtoMessage as per cTrader Open API spec
            var protoMsg = new ProtoMessage
            {
                PayloadType = (uint)payloadType,
                Payload = Google.Protobuf.ByteString.CopyFrom(innerPayload),
                ClientMsgId = Guid.NewGuid().ToString("N")
            };

            var protoMsgBytes = protoMsg.ToByteArray();
            var protoMsgHex = BitConverter.ToString(protoMsgBytes);
            _logger.LogDebug("ProtoMessage bytes: {Bytes}", protoMsgHex);
            Console.WriteLine($"=== CTRADER: Sending ProtoMessage {protoMsgHex} ===");

            // cTrader protocol: [4 bytes: message length (little-endian)] [ProtoMessage bytes]
            // Documentation note refers to big-endian platforms needing to reverse bytes. Windows already little-endian.
            var lengthBytes = BitConverter.GetBytes(protoMsgBytes.Length);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(lengthBytes);

            _logger.LogDebug("Sending length prefix bytes: {Bytes}", BitConverter.ToString(lengthBytes));

            // Write length prefix (big-endian network order)
            await _stream.WriteAsync(lengthBytes, 0, lengthBytes.Length, cancellationToken);
            
            // Write ProtoMessage
            await _stream.WriteAsync(protoMsgBytes, 0, protoMsgBytes.Length, cancellationToken);
            
            await _stream.FlushAsync(cancellationToken);

            _logger.LogDebug("Sent ProtoMessage: PayloadType={PayloadType}, InnerPayloadSize={InnerSize}, ProtoMessageSize={ProtoSize}", 
                payloadType, innerPayload.Length, protoMsgBytes.Length);

            return protoMsg.ClientMsgId!;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task SendMessageAsync<T>(T message, PayloadType payloadType, CancellationToken cancellationToken = default)
    {
        return SendMessageAsync(message, (int)payloadType, cancellationToken);
    }

    public async Task<T?> WaitForResponseAsync<T>(int payloadType, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingResponsesLock)
        {
            _pendingResponses[payloadType] = tcs;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            var payload = await tcs.Task.WaitAsync(cts.Token);

            // Allow callers to await raw payload bytes.
            if (typeof(T) == typeof(byte[]))
            {
                return (T)(object)payload;
            }
            
            // Deserialize based on type
            if (typeof(T).GetInterfaces().Contains(typeof(IMessage)))
            {
                var parser = typeof(T).GetProperty("Parser")?.GetValue(null) as MessageParser;
                return (T?)parser?.ParseFrom(payload);
            }
            else
            {
                var json = Encoding.UTF8.GetString(payload);
                return JsonConvert.DeserializeObject<T>(json);
            }
        }
        catch (OperationCanceledException)
        {
            // If the caller canceled, don't log as a timeout.
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Canceled waiting for response: PayloadType={PayloadType}", payloadType);
            }
            else
            {
                _logger.LogWarning("Timeout waiting for response: PayloadType={PayloadType}", payloadType);
            }
            return default;
        }
        finally
        {
            lock (_pendingResponsesLock)
            {
                _pendingResponses.Remove(payloadType);
            }
        }
    }

    public async Task<T?> WaitForResponseAsync<T>(int payloadType, string clientMsgId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientMsgId))
            throw new ArgumentException("clientMsgId is required", nameof(clientMsgId));

        var key = (payloadType, clientMsgId);
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pendingResponsesLock)
        {
            _pendingResponsesByClientMsgId[key] = tcs;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            var payload = await tcs.Task.WaitAsync(cts.Token);

            if (typeof(T) == typeof(byte[]))
            {
                return (T)(object)payload;
            }

            if (typeof(T).GetInterfaces().Contains(typeof(IMessage)))
            {
                var parser = typeof(T).GetProperty("Parser")?.GetValue(null) as MessageParser;
                return (T?)parser?.ParseFrom(payload);
            }

            var json = Encoding.UTF8.GetString(payload);
            return JsonConvert.DeserializeObject<T>(json);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Canceled waiting for response: PayloadType={PayloadType}, ClientMsgId={ClientMsgId}", payloadType, clientMsgId);
            }
            else
            {
                _logger.LogWarning("Timeout waiting for response: PayloadType={PayloadType}, ClientMsgId={ClientMsgId}", payloadType, clientMsgId);
            }
            return default;
        }
        finally
        {
            lock (_pendingResponsesLock)
            {
                _pendingResponsesByClientMsgId.Remove(key);
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && IsConnected && _stream != null)
            {
                // Read message length (4 bytes, little-endian from server)
                var lengthBuffer = new byte[4];
                var bytesRead = await ReadExactlyAsync(_stream, lengthBuffer, 0, 4, cancellationToken);
                
                if (bytesRead == 0)
                {
                    _logger.LogWarning("TCP connection closed by server");
                    await DisconnectAsync();
                    break;
                }
                
                var lengthBytesHex = BitConverter.ToString(lengthBuffer);
                _logger.LogInformation("Received length prefix bytes: {Bytes}", lengthBytesHex);
                Console.WriteLine($"=== CTRADER: Length prefix bytes {lengthBytesHex} ===");

                if (BitConverter.IsLittleEndian)
                    Array.Reverse(lengthBuffer);
                
                var messageLength = BitConverter.ToInt32(lengthBuffer, 0);
                
                if (messageLength <= 0 || messageLength > 1024 * 1024) // Max 1MB
                {
                    _logger.LogError("Invalid message length: {Length}", messageLength);
                    break;
                }

                // Read ProtoMessage bytes
                var protoMsgBuffer = new byte[messageLength];
                bytesRead = await ReadExactlyAsync(_stream, protoMsgBuffer, 0, messageLength, cancellationToken);
                
                if (bytesRead < messageLength)
                {
                    _logger.LogError("Incomplete ProtoMessage received: {Received}/{Expected}", bytesRead, messageLength);
                    break;
                }

                // Deserialize ProtoMessage
                var protoMsg = ProtoMessage.Parser.ParseFrom(protoMsgBuffer);

                _logger.LogDebug("Received ProtoMessage: PayloadType={PayloadType}, PayloadSize={Size}", 
                    protoMsg.PayloadType, protoMsg.Payload.Length);

                // Process the inner payload - convert ByteString to byte array
                var payloadBytes = protoMsg.Payload.ToByteArray();
                await ProcessMessageAsync((int)protoMsg.PayloadType, payloadBytes, payloadBytes.Length, protoMsg.ClientMsgId);
            }
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error in receive loop");
            }
        }
    }

    private async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, cancellationToken);
            if (read == 0)
                return totalRead; // Stream closed
            
            totalRead += read;
        }
        return totalRead;
    }

    private async Task ProcessMessageAsync(int payloadType, byte[] buffer, int length, string? clientMsgId)
    {
        try
        {
            // payload already separated; buffer is payload only
            var payload = buffer;

            // Some server environments appear to send payload types that don't match the official docs.
            // We normalize known mismatches so consumers waiting on official payload types still work.
            // Observed: SubscribeSpotsRes can arrive as 2142 instead of 2121.
            var normalizedPayloadType = payloadType;
            if (payloadType == 2142)
            {
                try
                {
                    _ = ProtoOASubscribeSpotsRes.Parser.ParseFrom(payload);
                    normalizedPayloadType = (int)PayloadType.ProtoOaSubscribeSpotsRes;
                }
                catch
                {
                    // Not a SubscribeSpotsRes; keep original payload type.
                }
            }

            _logger.LogInformation("📨 Received message: PayloadType={PayloadType}, Size={Size}", payloadType, length);

            // In our environment, cTrader returns order failures as PayloadType=2132.
            // Log and attempt to parse as ErrorRes so the real rejection reason is visible.
            if (payloadType == 2132)
            {
                _logger.LogWarning("🔍 PayloadType 2132 received - Hex: {Hex}", BitConverter.ToString(payload).Replace("-", " "));

                try
                {
                    var (errorCode, description, accountId) = TryParseErrorRes(payload);
                    if (!string.IsNullOrWhiteSpace(errorCode) || !string.IsNullOrWhiteSpace(description))
                    {
                        _logger.LogError("=== CTRADER ERROR (2132) ===");
                        _logger.LogError("ErrorCode: {ErrorCode}", errorCode);
                        _logger.LogError("Description: {Description}", description);
                        _logger.LogError("AccountId: {AccountId}", accountId);
                    }
                }
                catch
                {
                    // Best-effort parsing only
                }
            }

            // Check for error responses and log them
            if (payloadType == (int)PayloadType.ProtoOaErrorRes)
            {
                _logger.LogError("=== CTRADER ERROR RESPONSE (PayloadType={PayloadType}) ===", payloadType);
                _logger.LogError("Raw payload hex: {Hex}", BitConverter.ToString(payload).Replace("-", " "));
                Console.WriteLine($"=== CTRADER ERROR RESPONSE (PayloadType={payloadType}) ===");
                Console.WriteLine($"Raw payload hex: {BitConverter.ToString(payload).Replace("-", " ")}");
                
                try
                {
                    var (errorCode, description, accountId) = TryParseErrorRes(payload);
                    _logger.LogError("ErrorCode: {ErrorCode}", errorCode);
                    _logger.LogError("Description: {Description}", description);
                    _logger.LogError("AccountId: {AccountId}", accountId);
                    Console.WriteLine($"ErrorCode: {errorCode}");
                    Console.WriteLine($"Description: {description}");
                    Console.WriteLine($"AccountId: {accountId}");
                    Console.WriteLine($"================================");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to parse error response");
                    Console.WriteLine($"Failed to parse error response: {ex.Message}");
                }
            }

            lock (_pendingResponsesLock)
            {
                if (!string.IsNullOrWhiteSpace(clientMsgId) &&
                    _pendingResponsesByClientMsgId.TryGetValue((normalizedPayloadType, clientMsgId), out var correlatedTcs))
                {
                    correlatedTcs.TrySetResult(payload);
                }

                // Legacy fallback: check if someone is waiting by payload type only.
                if (_pendingResponses.TryGetValue(normalizedPayloadType, out var tcs))
                {
                    tcs.TrySetResult(payload);
                }
            }

            // Fire event for message received
            var message = new CTraderMessage
            {
                PayloadType = normalizedPayloadType,
                Payload = payload,
                ClientMsgId = clientMsgId,
                ReceivedAt = DateTime.UtcNow
            };

            MessageReceived?.Invoke(this, message);

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message");
        }
    }

    private static (string? ErrorCode, string? Description, long? AccountId) TryParseErrorRes(byte[] payload)
    {
        // Observed wire format in this repo's logs:
        // 0x12 => field 2 (string) errorCode (e.g., TRADING_BAD_VOLUME)
        // 0x28 => field 5 (varint) ctidTraderAccountId
        // 0x3A => field 7 (string) description
        string? errorCode = null;
        string? description = null;
        long? accountId = null;

        var input = new CodedInputStream(payload);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 0x12:
                    errorCode = input.ReadString();
                    break;
                case 0x28:
                    accountId = input.ReadInt64();
                    break;
                case 0x3A:
                    description = input.ReadString();
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }

        return (errorCode, description, accountId);
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && IsConnected)
            {
                await Task.Delay(TimeSpan.FromSeconds(_heartbeatInterval), cancellationToken);

                if (IsConnected)
                {
                    var heartbeat = new ProtoHeartbeatEvent();
                    await SendMessageAsync(heartbeat, 51, cancellationToken);  // 51 is HEARTBEAT_EVENT
                    _logger.LogDebug("Heartbeat sent");
                }
            }
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error in heartbeat loop");
            }
        }
    }

    public void Dispose()
    {
        _receiveCts?.Cancel();
        _receiveCts?.Dispose();
        _stream?.Dispose();
        _tcpClient?.Dispose();
        _sendLock?.Dispose();
    }
}

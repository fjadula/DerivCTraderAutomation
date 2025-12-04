using System.Net.WebSockets;
using System.Text;
using DerivCTrader.Infrastructure.CTrader.Models;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Infrastructure.CTrader;

internal class CTraderAuthenticator
{
    private readonly ILogger<CTraderAuthenticator> _logger;
    private readonly string _clientId;
    private readonly string _clientSecret;

    public CTraderAuthenticator(ILogger<CTraderAuthenticator> logger, string clientId, string clientSecret)
    {
        _logger = logger;
        _clientId = clientId;
        _clientSecret = clientSecret;
    }

    public async Task<bool> AuthenticateApplicationAsync(ClientWebSocket webSocket)
    {
        try
        {
            var authReq = new ProtoOAApplicationAuthReq
            {
                ClientId = _clientId,
                ClientSecret = _clientSecret
            };

            await SendMessageAsync(webSocket, ProtoOAPayloadType.ProtoOaApplicationAuthReq, authReq);
            _logger.LogInformation("Application authentication request sent");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to authenticate application");
            return false;
        }
    }

    public async Task<bool> AuthenticateAccountAsync(ClientWebSocket webSocket, long accountId, string accessToken)
    {
        try
        {
            var accountAuthReq = new ProtoOAAccountAuthReq
            {
                CtidTraderAccountId = accountId,
                AccessToken = accessToken
            };

            await SendMessageAsync(webSocket, ProtoOAPayloadType.ProtoOaAccountAuthReq, accountAuthReq);
            _logger.LogInformation("Account authentication request sent for account {AccountId}", accountId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to authenticate account {AccountId}", accountId);
            return false;
        }
    }

    private async Task SendMessageAsync(ClientWebSocket webSocket, ProtoOAPayloadType payloadType, IMessage message)
    {
        var payload = message.ToByteArray();
        var messageBytes = new byte[sizeof(int) + payload.Length];

        // Write payload type (4 bytes)
        BitConverter.GetBytes((int)payloadType).CopyTo(messageBytes, 0);
        // Write payload
        payload.CopyTo(messageBytes, sizeof(int));

        await webSocket.SendAsync(
            new ArraySegment<byte>(messageBytes),
            WebSocketMessageType.Binary,
            true,
            CancellationToken.None);
    }
}
using System.Net.Http.Json;
using System.Text.Json;
using DerivCTrader.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Infrastructure.Notifications;

public sealed class TelegramNotifier : ITelegramNotifier
{
    private readonly ILogger<TelegramNotifier> _logger;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;

    public TelegramNotifier(ILogger<TelegramNotifier> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task SendTradeMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        await SendTradeMessageInternalAsync(message, replyToMessageId: null, cancellationToken);
    }

    public async Task<int?> SendTradeMessageWithIdAsync(string message, CancellationToken cancellationToken = default)
    {
        return await SendTradeMessageInternalAsync(message, replyToMessageId: null, cancellationToken);
    }

    public async Task<int?> SendTradeMessageAsync(string message, int replyToMessageId, CancellationToken cancellationToken = default)
    {
        return await SendTradeMessageInternalAsync(message, replyToMessageId, cancellationToken);
    }

    private async Task<int?> SendTradeMessageInternalAsync(string message, int? replyToMessageId, CancellationToken cancellationToken)
    {
        try
        {
            var token = _configuration.GetSection("Telegram")["SignalToken"]
                        ?? _configuration.GetSection("Telegram")["BotToken"];
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("Telegram SignalToken/BotToken not configured; skipping message");
                return null;
            }

            var chatId = _configuration.GetSection("Telegram")["TradeChatId"]
                         ?? _configuration.GetSection("Telegram")["AlertChatId"];

            if (string.IsNullOrWhiteSpace(chatId))
            {
                _logger.LogWarning("Telegram TradeChatId/AlertChatId not configured; skipping message");
                return null;
            }

            var url = $"https://api.telegram.org/bot{token}/sendMessage";

            // Build payload with optional reply_to_message_id for threading
            var payload = new Dictionary<string, object>
            {
                ["chat_id"] = chatId,
                ["text"] = message,
                ["disable_web_page_preview"] = true
            };

            if (replyToMessageId.HasValue)
            {
                payload["reply_to_message_id"] = replyToMessageId.Value;
                _logger.LogInformation("Sending Telegram message as reply to message_id={ReplyTo}", replyToMessageId.Value);
            }

            var res = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken);
            var body = await res.Content.ReadAsStringAsync(cancellationToken);

            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Telegram sendMessage failed: {Status} {Body}", res.StatusCode, body);
                return null;
            }

            // Parse response to extract message_id
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.False)
                {
                    _logger.LogWarning("Telegram sendMessage returned ok=false: {Body}", body);
                    return null;
                }

                // Extract message_id from response: {"ok":true,"result":{"message_id":123,...}}
                if (doc.RootElement.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("message_id", out var messageIdProp))
                {
                    var messageId = messageIdProp.GetInt32();
                    _logger.LogInformation("Telegram sendMessage succeeded: ChatId={ChatId}, MessageId={MessageId}", chatId, messageId);
                    return messageId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse message_id from Telegram response: {Body}", body);
            }

            _logger.LogInformation("Telegram sendMessage succeeded: ChatId={ChatId}", chatId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram sendMessage exception");
            return null;
        }
    }
}

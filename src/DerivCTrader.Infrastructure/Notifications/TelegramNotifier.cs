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
        try
        {
            var token = _configuration.GetSection("Telegram")["SignalToken"]
                        ?? _configuration.GetSection("Telegram")["BotToken"];
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("Telegram SignalToken/BotToken not configured; skipping message");
                return;
            }

            var chatId = _configuration.GetSection("Telegram")["TradeChatId"]
                         ?? _configuration.GetSection("Telegram")["AlertChatId"];

            if (string.IsNullOrWhiteSpace(chatId))
            {
                _logger.LogWarning("Telegram TradeChatId/AlertChatId not configured; skipping message");
                return;
            }

            var url = $"https://api.telegram.org/bot{token}/sendMessage";

            var payload = new
            {
                chat_id = chatId,
                text = message,
                disable_web_page_preview = true
            };

            var res = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken);
            var body = await res.Content.ReadAsStringAsync(cancellationToken);

            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Telegram sendMessage failed: {Status} {Body}", res.StatusCode, body);
                return;
            }

            // Telegram typically returns HTTP 200 with {"ok":true}. Be defensive if ok=false.
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.False)
                {
                    _logger.LogWarning("Telegram sendMessage returned ok=false: {Body}", body);
                    return;
                }
            }
            catch
            {
                // Ignore parse errors; a 200 response is usually fine.
            }

            _logger.LogInformation("Telegram sendMessage succeeded: ChatId={ChatId}", chatId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram sendMessage exception");
        }
    }
}

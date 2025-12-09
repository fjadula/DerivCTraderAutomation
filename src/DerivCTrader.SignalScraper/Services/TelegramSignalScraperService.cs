using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TL;
using WTelegram;

namespace DerivCTrader.SignalScraper.Services;

public class TelegramSignalScraperService : BackgroundService
{
    private readonly ILogger<TelegramSignalScraperService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IEnumerable<ISignalParser> _parsers;
    private readonly ITradeRepository _repository;
    private Client? _client1;
    private Client? _client2;
    private readonly Dictionary<long, string> _channelMappings;

    public TelegramSignalScraperService(
        ILogger<TelegramSignalScraperService> logger,
        IConfiguration configuration,
        IEnumerable<ISignalParser> parsers,
        ITradeRepository repository)
    {
        _logger = logger;
        _configuration = configuration;
        _parsers = parsers;
        _repository = repository;

        // Build channel ID mappings
        _channelMappings = new Dictionary<long, string>();
        // Build channel ID mappings - handle ALL channel formats
        _channelMappings = new Dictionary<long, string>();
        var channels = _configuration.GetSection("ProviderChannels");
        foreach (var channel in channels.GetChildren())
        {
            var channelValue = channel.Value;
            if (string.IsNullOrEmpty(channelValue))
                continue;

            // Skip username-based channels (e.g., @ChannelName)
            if (channelValue.StartsWith("@"))
            {
                _logger.LogWarning("Skipping username-based channel: {Channel} - numeric ID required", channelValue);
                continue;
            }

            // Handle any negative channel ID
            if (channelValue.StartsWith("-") && long.TryParse(channelValue, out var fullChannelId))
            {
                // Extract the numeric part based on format
                long mappingKey;
                if (channelValue.StartsWith("-100"))
                {
                    // Standard supergroup: -1001234567890 → map by last digits (1234567890)
                    var numericPart = channelValue.Substring(4);
                    if (long.TryParse(numericPart, out var channelId))
                    {
                        mappingKey = channelId;
                    }
                    else
                    {
                        _logger.LogWarning("Failed to parse channel ID: {Channel}", channelValue);
                        continue;
                    }
                }
                else
                {
                    // Other formats (-14xxx, -13xxx, -22xxx, etc.) → map by digits after -1
                    // This handles: -1476865523, -1392143914, -22xxxxxxxxx, etc.
                    var numericPart = channelValue.Substring(2); // Remove "-1"
                    if (long.TryParse(numericPart, out var channelId))
                    {
                        mappingKey = channelId;
                    }
                    else
                    {
                        _logger.LogWarning("Failed to parse channel ID: {Channel}", channelValue);
                        continue;
                    }
                }

                _channelMappings[mappingKey] = channelValue;
                _logger.LogInformation("Mapped channel {Key} -> {Value}", mappingKey, channelValue);
            }
            else
            {
                _logger.LogWarning("Invalid channel format: {Channel} (must start with - and be numeric)", channelValue);
            }
        }

        _logger.LogInformation("Total channels mapped: {Count}", _channelMappings.Count);
    }

        _logger.LogInformation("Total channels mapped: {Count}", _channelMappings.Count);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Telegram Signal Scraper Service starting...");
        Console.WriteLine("=== SERVICE EXECUTEASY NC CALLED ===");

        try
        {
            // Initialize WTelegram clients
            await InitializeClientsAsync();

            // Start listening for messages
            await ListenForMessagesAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in Telegram Signal Scraper");
            Console.WriteLine($"=== FATAL ERROR IN SERVICE: {ex.Message} ===");
            Console.WriteLine(ex.StackTrace);

            // Keep running so the host doesn't exit
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    private async Task InitializeClientsAsync()
    {
        _logger.LogInformation("Starting client initialization...");
        Console.WriteLine("=== INITIALIZING CLIENTS ===");

        // Initialize Account 1
        _logger.LogInformation("Initializing Account 1...");
        Console.WriteLine("=== ACCOUNT 1 INIT ===");

        var account1 = _configuration.GetSection("Telegram:WTelegram:Account1");
        _client1 = new Client(config =>
        {
            return config switch
            {
                "api_id" => account1["ApiId"],
                "api_hash" => account1["ApiHash"],
                "phone_number" => account1["PhoneNumber"],
                "password" => account1["Password"],
                "session_pathname" => "DerivCTrader",
                _ => null
            };
        });

        try
        {
            await _client1.LoginUserIfNeeded();
            _logger.LogInformation("WTelegram Account 1 logged in successfully");
            Console.WriteLine("=== ACCOUNT 1 LOGGED IN ===");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to login to WTelegram Account 1");
            Console.WriteLine($"=== ACCOUNT 1 LOGIN FAILED: {ex.Message} ===");
            throw;
        }

        // Initialize Account 2 (optional - continue if fails)
        _logger.LogInformation("Initializing Account 2...");
        Console.WriteLine("=== ACCOUNT 2 INIT ===");

        var account2 = _configuration.GetSection("Telegram:WTelegram:Account2");
        try
        {
            _client2 = new Client(config =>
            {
                return config switch
                {
                    "api_id" => account2["ApiId"],
                    "api_hash" => account2["ApiHash"],
                    "phone_number" => account2["PhoneNumber"],
                    "password" => account2["Password"],
                    "session_pathname" => "DerivCTrader2",
                    _ => null
                };
            });

            await _client2.LoginUserIfNeeded();
            _logger.LogInformation("WTelegram Account 2 logged in successfully");
            Console.WriteLine("=== ACCOUNT 2 LOGGED IN ===");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to login to WTelegram Account 2 - continuing with Account 1 only");
            Console.WriteLine($"=== ACCOUNT 2 LOGIN FAILED (CONTINUING): {ex.Message} ===");
            _client2?.Dispose();
            _client2 = null;
        }

        _logger.LogInformation("Client initialization complete. Account 1: {A1}, Account 2: {A2}",
            _client1 != null, _client2 != null);
        Console.WriteLine($"=== CLIENTS INITIALIZED: Account1={_client1 != null}, Account2={_client2 != null} ===");
    }

    private async Task ListenForMessagesAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("=== STARTING LISTEN LOOP ===");

        if (_client1 == null)
        {
            var error = "Client 1 not initialized";
            Console.WriteLine($"=== ERROR: {error} ===");
            throw new InvalidOperationException(error);
        }

        // Set up message handler for Account 1
        _client1.OnUpdate += async updates =>
        {
            try
            {
                foreach (var update in updates.UpdateList)
                {
                    await HandleUpdateAsync(update, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Account 1 update handler");
            }
        };

        // Set up message handler for Account 2 (if logged in)
        if (_client2 != null)
        {
            _client2.OnUpdate += async updates =>
            {
                try
                {
                    foreach (var update in updates.UpdateList)
                    {
                        await HandleUpdateAsync(update, stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in Account 2 update handler");
                }
            };
            _logger.LogInformation("Listening for Telegram messages from configured channels (2 accounts)...");
            Console.WriteLine("=== LISTENING WITH 2 ACCOUNTS ===");
        }
        else
        {
            _logger.LogInformation("Listening for Telegram messages from configured channels (Account 1 only)...");
            Console.WriteLine("=== LISTENING WITH 1 ACCOUNT ===");
        }

        // Keep the service running
        Console.WriteLine("=== ENTERING INFINITE LOOP ===");
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }

        Console.WriteLine("=== CANCELLATION REQUESTED, EXITING ===");
    }

    private async Task HandleUpdateAsync(Update update, CancellationToken cancellationToken)
    {
        try
        {
            // Log every update received
            _logger.LogInformation("🔔 UPDATE RECEIVED: Type = {Type}", update.GetType().Name);
            Console.WriteLine($"🔔 UPDATE: {update.GetType().Name}");

            if (update is UpdateNewMessage { message: Message message })
            {
                // Extract channel ID
                var peer = message.peer_id;
                long channelId = 0;
                if (peer is PeerChannel peerChannel)
                {
                    channelId = peerChannel.channel_id;
                }
                else if (peer is PeerChat peerChat)
                {
                    channelId = peerChat.chat_id;
                }

                // Log the raw message details
                _logger.LogInformation("📥 RAW MESSAGE | ChannelId: {ChannelId}", channelId);
                Console.WriteLine($"📥 Channel: {channelId}");
                Console.WriteLine($"📝 FULL MESSAGE:\n{message.message}\n");

                // Check if this is a monitored channel
                var providerChannelId = _channelMappings.ContainsKey(channelId)
                    ? _channelMappings[channelId]
                    : $"-100{channelId}";

                // Log the provider lookup
                _logger.LogInformation("🔍 Looking up provider: {Provider} (Raw ID: {RawId})",
                    providerChannelId, channelId);
                Console.WriteLine($"🔍 Looking for: {providerChannelId} (from raw: {channelId})");

                var config = await _repository.GetProviderConfigAsync(providerChannelId);
                if (config == null)
                {
                    _logger.LogWarning("❌ NOT A CONFIGURED CHANNEL: {Channel} | Raw ID: {RawId}",
                        providerChannelId, channelId);
                    Console.WriteLine($"❌ Channel {providerChannelId} not in database");
                    return;
                }

                // Confirm config found
                _logger.LogInformation("✅ FOUND CONFIG: {Provider}", config.ProviderName);
                Console.WriteLine($"✅ Config found: {config.ProviderName}");

                _logger.LogInformation("Received message from channel {Channel}: {Preview}",
                    config.ProviderName,
                    message.message?.Substring(0, Math.Min(50, message.message?.Length ?? 0)));

                // Extract text and images
                string? messageText = message.message;
                byte[]? imageData = null;
                if (message.media is MessageMediaPhoto { photo: Photo photo })
                {
                    imageData = await DownloadPhotoAsync(photo);
                }

                // Find appropriate parser
                _logger.LogInformation("🔎 Looking for parser for channel: {Channel}", providerChannelId);
                Console.WriteLine($"🔎 Searching for parser for: {providerChannelId}");

                var parser = _parsers.FirstOrDefault(p => p.CanParse(providerChannelId));
                if (parser == null)
                {
                    _logger.LogWarning("❌ No parser found for channel {Channel}", providerChannelId);
                    Console.WriteLine($"❌ No parser for {providerChannelId} (Have {_parsers.Count()} parsers total)");

                    foreach (var p in _parsers)
                    {
                        _logger.LogInformation("  Available parser: {Type}", p.GetType().Name);
                        Console.WriteLine($"  Parser: {p.GetType().Name}");
                    }
                    return;
                }

                // Log which parser matched
                _logger.LogInformation("✅ PARSER FOUND: {Parser}", parser.GetType().Name);
                Console.WriteLine($"✅ Using parser: {parser.GetType().Name}");

                // Parse signal
                _logger.LogInformation("🔨 Parsing signal...");
                Console.WriteLine("🔨 Attempting to parse...");

                var parsedSignal = await parser.ParseAsync(messageText ?? "", providerChannelId, imageData);

                if (parsedSignal != null)
                {
                    _logger.LogInformation("✅ Successfully parsed signal: {Asset} {Direction} @ {Entry} | TP: {TP} | SL: {SL}",
                        parsedSignal.Asset,
                        parsedSignal.Direction,
                        parsedSignal.EntryPrice,
                        parsedSignal.TakeProfit,
                        parsedSignal.StopLoss);
                    Console.WriteLine($"✅ PARSED SIGNAL:");
                    Console.WriteLine($"   Asset: {parsedSignal.Asset}");
                    Console.WriteLine($"   Direction: {parsedSignal.Direction}");
                    Console.WriteLine($"   Entry: {parsedSignal.EntryPrice}");
                    Console.WriteLine($"   Take Profit: {parsedSignal.TakeProfit}");
                    Console.WriteLine($"   Stop Loss: {parsedSignal.StopLoss}");

                    // 🆕 SAVE TO DATABASE
                    try
                    {
                        var signalId = await _repository.SaveParsedSignalAsync(parsedSignal);
                        
                        _logger.LogInformation("💾 Signal saved to queue: SignalId={SignalId}", signalId);
                        Console.WriteLine($"💾 SAVED TO QUEUE: Signal #{signalId}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to save signal to database");
                        Console.WriteLine($"❌ DATABASE SAVE FAILED: {ex.Message}");
                    }
                }
                else
                {
                    _logger.LogWarning("❌ Parser returned NULL - failed to parse signal");
                    Console.WriteLine("❌ Parsing failed - returned null");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Telegram update");
            Console.WriteLine($"💥 ERROR: {ex.Message}");
        }
    }

    private async Task<byte[]?> DownloadPhotoAsync(Photo photo)
    {
        // Placeholder implementation
        return await Task.FromResult<byte[]?>(null);
    }

    public override void Dispose()
    {
        Console.WriteLine("=== DISPOSING CLIENTS ===");
        _client1?.Dispose();
        _client2?.Dispose();
        base.Dispose();
    }
}
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Application.Parsers;
using DerivCTrader.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using Tesseract;
using TL;
using WTelegram;

namespace DerivCTrader.SignalScraper.Services;

public class TelegramSignalScraperService : BackgroundService
{
    private readonly ILogger<TelegramSignalScraperService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IEnumerable<ISignalParser> _parsers;
    private readonly ITradeRepository _repository;
    private readonly IDashaTradeRepository _dashaRepository;
    private readonly CmflixParser _cmflixParser;
    private readonly IzintzikaDerivParser _izintzikaDerivParser;
    private Client? _client1;
    private Client? _client2;
    private readonly Dictionary<long, string> _channelMappings;

    // DashaTrade channel ID for selective martingale routing
    private const string DASHA_TRADE_CHANNEL_ID = "-1001570351142";

    // CMFLIX Gold Signals channel ID for scheduled signals
    private const string CMFLIX_CHANNEL_ID = "-1001473818334";

    // Reduce log noise: warn once per unknown channel, then downgrade repeats.
    private readonly HashSet<string> _unknownChannelWarned = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _unknownChannelWarnedLock = new();

    private static readonly HttpClient Http = new();
    private static readonly SemaphoreSlim OcrLock = new(1, 1);

    public TelegramSignalScraperService(
        ILogger<TelegramSignalScraperService> logger,
        IConfiguration configuration,
        IEnumerable<ISignalParser> parsers,
        ITradeRepository repository,
        IDashaTradeRepository dashaRepository,
        CmflixParser cmflixParser,
        IzintzikaDerivParser izintzikaDerivParser)
    {
        _logger = logger;
        _configuration = configuration;
        _parsers = parsers;
        _repository = repository;
        _dashaRepository = dashaRepository;
        _cmflixParser = cmflixParser;
        _izintzikaDerivParser = izintzikaDerivParser;

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
                    // Other formats: map by absolute numeric ID (strip leading '-')
                    // Example: -1628868943 -> 1628868943
                    // This matches WTelegram's PeerChannel.channel_id / PeerChat.chat_id values.
                    var numericPart = channelValue.Substring(1);
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
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Telegram Signal Scraper stopping (cancellation requested)");
        }
        catch (TaskCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Telegram Signal Scraper stopping (task cancelled)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in Telegram Signal Scraper");
            Console.WriteLine($"=== FATAL ERROR IN SERVICE: {ex.Message} ===");
            Console.WriteLine(ex.StackTrace);

            // Keep running so the host doesn't exit
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), CancellationToken.None);
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
                    await HandleUpdateAsync(_client1, update, stoppingToken);
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
                        await HandleUpdateAsync(_client2, update, stoppingToken);
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
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        Console.WriteLine("=== CANCELLATION REQUESTED, EXITING ===");
    }

    private async Task HandleUpdateAsync(Client? client, Update update, CancellationToken cancellationToken)
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
                _logger.LogInformation("📥 RAW MESSAGE | ChannelId: {ChannelId}, MessageId: {MessageId}", channelId, message.id);
                Console.WriteLine($"📥 Channel: {channelId}, MessageId: {message.id}");
                Console.WriteLine($"📝 FULL MESSAGE:\n{message.message}\n");

                // Check if this is a monitored channel
                // If mapping exists, use configured value. Otherwise try both common formats.
                var providerChannelId = _channelMappings.ContainsKey(channelId)
                    ? _channelMappings[channelId]
                    : $"-{channelId}";

                // Log the provider lookup
                _logger.LogInformation("🔍 Looking up provider: {Provider} (Raw ID: {RawId})",
                    providerChannelId, channelId);
                Console.WriteLine($"🔍 Looking for: {providerChannelId} (from raw: {channelId})");

                // If not found under "-{id}" then also try "-100{id}" (supergroup format)
                var config = await _repository.GetProviderConfigAsync(providerChannelId)
                             ?? await _repository.GetProviderConfigAsync($"-100{channelId}");
                if (config == null)
                {
                    var firstTime = false;
                    lock (_unknownChannelWarnedLock)
                    {
                        firstTime = _unknownChannelWarned.Add(providerChannelId);
                    }

                    if (firstTime)
                    {
                        _logger.LogWarning("❌ NOT A CONFIGURED CHANNEL: {Channel} | Raw ID: {RawId}",
                            providerChannelId, channelId);
                        Console.WriteLine($"❌ Channel {providerChannelId} not in database");
                    }
                    else
                    {
                        _logger.LogDebug("Skipping unconfigured channel (repeat): {Channel} | Raw ID: {RawId}",
                            providerChannelId, channelId);
                    }
                    return;
                }

                // Ensure providerChannelId matches the DB row we found (important for parser routing)
                providerChannelId = config.ProviderChannelId;

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
                    imageData = await DownloadPhotoAsync(client, photo, cancellationToken);

                    // If the message is image-only (common in copy-protected channels), OCR it
                    if (string.IsNullOrWhiteSpace(messageText) && imageData != null)
                    {
                        var ocrText = await TryOcrAsync(imageData, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(ocrText))
                        {
                            messageText = ocrText;
                            _logger.LogInformation("🧾 OCR extracted text (len={Len})", ocrText.Length);
                            Console.WriteLine($"🧾 OCR TEXT:\n{ocrText}\n");
                        }
                        else
                        {
                            _logger.LogWarning("OCR returned empty text for photo message");
                        }
                    }
                }

                // 🆕 CMFLIX BATCH SIGNALS - Handle before normal parsers
                // CMFLIX sends batch scheduled signals that need special handling
                if (_cmflixParser.CanParse(providerChannelId, messageText ?? ""))
                {
                    _logger.LogInformation("📅 CMFLIX batch signal detected, parsing scheduled signals");
                    Console.WriteLine("📅 CMFLIX batch signal detected");

                    var scheduledSignals = _cmflixParser.ParseBatch(messageText ?? "", message.id);

                    if (scheduledSignals.Count > 0)
                    {
                        var savedCount = 0;
                        foreach (var signal in scheduledSignals)
                        {
                            try
                            {
                                var signalId = await _repository.SaveParsedSignalAsync(signal);
                                savedCount++;
                                _logger.LogDebug("Saved CMFLIX signal: {Asset} {Direction} at {ScheduledTime:HH:mm} UTC",
                                    signal.Asset, signal.Direction, signal.ScheduledAtUtc);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to save CMFLIX signal {Asset}", signal.Asset);
                            }
                        }

                        _logger.LogInformation("💾 Saved {SavedCount}/{TotalCount} CMFLIX scheduled signals",
                            savedCount, scheduledSignals.Count);
                        Console.WriteLine($"💾 Saved {savedCount}/{scheduledSignals.Count} CMFLIX scheduled signals");
                        return; // Done processing this message
                    }
                    else
                    {
                        _logger.LogWarning("CMFLIX parser matched but returned 0 signals");
                    }
                }

                // 🆕 IZINTZIKADERIV BATCH SIGNALS - Handle before normal parsers
                // IzintzikaDeriv sends multiple entry order signals per message
                if (_izintzikaDerivParser.CanParse(providerChannelId, messageText ?? ""))
                {
                    _logger.LogInformation("📊 IzintzikaDeriv batch signal detected, parsing entry orders");
                    Console.WriteLine("📊 IzintzikaDeriv batch signal detected");

                    var entrySignals = _izintzikaDerivParser.ParseBatch(messageText ?? "", message.id);

                    if (entrySignals.Count > 0)
                    {
                        var savedCount = 0;
                        foreach (var signal in entrySignals)
                        {
                            try
                            {
                                var signalId = await _repository.SaveParsedSignalAsync(signal);
                                savedCount++;
                                _logger.LogDebug("Saved IzintzikaDeriv signal: {Asset} {Direction} @ {Entry}",
                                    signal.Asset, signal.Direction, signal.EntryPrice);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to save IzintzikaDeriv signal {Asset}", signal.Asset);
                            }
                        }

                        _logger.LogInformation("💾 Saved {SavedCount}/{TotalCount} IzintzikaDeriv entry signals",
                            savedCount, entrySignals.Count);
                        Console.WriteLine($"💾 Saved {savedCount}/{entrySignals.Count} IzintzikaDeriv entry signals");
                        return; // Done processing this message
                    }
                    else
                    {
                        _logger.LogWarning("IzintzikaDeriv parser matched but returned 0 signals");
                    }
                }

                // Find appropriate parser
                _logger.LogInformation("🔎 Looking for parser for channel: {Channel}", providerChannelId);
                Console.WriteLine($"🔎 Searching for parser for: {providerChannelId}");

                var candidateParsers = _parsers.Where(p => p.CanParse(providerChannelId)).ToList();
                if (candidateParsers.Count == 0)
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

                _logger.LogInformation("✅ Found {Count} candidate parser(s) for channel {Channel}", candidateParsers.Count, providerChannelId);
                Console.WriteLine($"✅ Found {candidateParsers.Count} candidate parser(s)");

                ParsedSignal? parsedSignal = null;
                ISignalParser? winningParser = null;

                foreach (var candidate in candidateParsers)
                {
                    _logger.LogInformation("🔨 Trying parser: {Parser}", candidate.GetType().Name);
                    Console.WriteLine($"🔨 Trying parser: {candidate.GetType().Name}");

                    parsedSignal = await candidate.ParseAsync(messageText ?? "", providerChannelId, imageData);
                    if (parsedSignal != null)
                    {
                        winningParser = candidate;
                        break;
                    }
                }

                if (winningParser != null)
                {
                    _logger.LogInformation("✅ PARSER SUCCEEDED: {Parser}", winningParser.GetType().Name);
                    Console.WriteLine($"✅ Parser succeeded: {winningParser.GetType().Name}");
                }

                if (parsedSignal != null)
                {
                    // Capture the Telegram message ID for reply threading
                    parsedSignal.TelegramMessageId = message.id;
                    
                    _logger.LogInformation("✅ Successfully parsed signal: {Asset} {Direction} @ {Entry} | TP: {TP} | SL: {SL} | TelegramMsgId: {MsgId}",
                        parsedSignal.Asset,
                        parsedSignal.Direction,
                        parsedSignal.EntryPrice,
                        parsedSignal.TakeProfit,
                        parsedSignal.StopLoss,
                        parsedSignal.TelegramMessageId);
                    Console.WriteLine($"✅ PARSED SIGNAL:");
                    Console.WriteLine($"   Asset: {parsedSignal.Asset}");
                    Console.WriteLine($"   Direction: {parsedSignal.Direction}");
                    Console.WriteLine($"   Entry: {parsedSignal.EntryPrice}");
                    Console.WriteLine($"   Take Profit: {parsedSignal.TakeProfit}");
                    Console.WriteLine($"   Stop Loss: {parsedSignal.StopLoss}");
                    Console.WriteLine($"   Telegram Message ID: {parsedSignal.TelegramMessageId}");

                    // 🆕 SAVE TO DATABASE
                    try
                    {
                        // Check if this is a DashaTrade signal for selective martingale routing
                        if (providerChannelId == DASHA_TRADE_CHANNEL_ID)
                        {
                            // Route to DashaPendingSignals for selective martingale processing
                            await SaveDashaTradePendingSignalAsync(parsedSignal);
                        }
                        else
                        {
                            // Normal signal - save to ParsedSignalsQueue
                            var signalId = await _repository.SaveParsedSignalAsync(parsedSignal);

                            _logger.LogInformation("💾 Signal saved to queue: SignalId={SignalId}", signalId);
                            Console.WriteLine($"💾 SAVED TO QUEUE: Signal #{signalId}");
                        }
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

    private async Task<byte[]?> DownloadPhotoAsync(Client? client, Photo photo, CancellationToken cancellationToken)
    {
        if (client == null)
        {
            _logger.LogWarning("DownloadPhotoAsync called but client was null");
            return null;
        }

        try
        {
            await using var ms = new MemoryStream();
            // WTelegram helper: download full photo content
            await client.DownloadFileAsync(photo, ms, progress: (_, __) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
            });

            return ms.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download photo from Telegram");
            return null;
        }
    }

    private async Task<string?> TryOcrAsync(byte[] imageData, CancellationToken cancellationToken)
    {
        // OCR is optional: it will attempt to download eng.traineddata automatically if missing
        // and then run Tesseract over the image bytes.

        await OcrLock.WaitAsync(cancellationToken);
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var tessdataDir = Path.Combine(baseDir, "tessdata");
            Directory.CreateDirectory(tessdataDir);

            var trainedDataPath = Path.Combine(tessdataDir, "eng.traineddata");
            if (!File.Exists(trainedDataPath))
            {
                // Use tessdata_fast for smaller download
                var url = "https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata";
                _logger.LogInformation("Downloading OCR traineddata: {Url}", url);
                using var resp = await Http.GetAsync(url, cancellationToken);
                resp.EnsureSuccessStatusCode();
                var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
                await File.WriteAllBytesAsync(trainedDataPath, bytes, cancellationToken);
                _logger.LogInformation("Downloaded OCR traineddata to {Path}", trainedDataPath);
            }

            using var engine = new TesseractEngine(tessdataDir, "eng", EngineMode.Default);
            using var pix = Pix.LoadFromMemory(imageData);
            using var page = engine.Process(pix);

            var text = page.GetText();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            // Normalize whitespace a bit for regex parsers
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            return text.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OCR failed");
            return null;
        }
        finally
        {
            OcrLock.Release();
        }
    }

    /// <summary>
    /// Saves a DashaTrade signal to DashaPendingSignals table for selective martingale processing.
    /// Entry price will be filled in by DashaTradeExecutionService shortly after.
    /// </summary>
    private async Task SaveDashaTradePendingSignalAsync(ParsedSignal parsedSignal)
    {
        var now = DateTime.UtcNow;

        // Parse timeframe to get expiry minutes
        var expiryMinutes = ParseTimeframeToMinutes(parsedSignal.Timeframe ?? "M15");

        // Map direction: Buy -> UP, Sell -> DOWN
        var direction = parsedSignal.Direction.ToString().ToUpperInvariant() switch
        {
            "BUY" => "UP",
            "SELL" => "DOWN",
            _ => parsedSignal.Direction.ToString().ToUpperInvariant()
        };

        // Check for duplicate
        var exists = await _dashaRepository.SignalExistsAsync(
            parsedSignal.Asset,
            direction,
            now,
            parsedSignal.ProviderChannelId);

        if (exists)
        {
            _logger.LogInformation("[DashaTrade] Duplicate signal detected, skipping: {Asset} {Direction}", parsedSignal.Asset, direction);
            Console.WriteLine($"[DashaTrade] Duplicate signal, skipping");
            return;
        }

        // Create pending signal (entry price = 0, will be filled by TradeExecutor)
        var pendingSignal = new DashaPendingSignal
        {
            ProviderChannelId = parsedSignal.ProviderChannelId,
            ProviderName = parsedSignal.ProviderName ?? "DashaTrade",
            Asset = parsedSignal.Asset,
            Direction = direction,
            Timeframe = parsedSignal.Timeframe ?? "M15",
            ExpiryMinutes = expiryMinutes,
            EntryPrice = 0, // Will be filled by DashaTradeExecutionService
            SignalReceivedAt = now,
            ExpiryAt = now.AddMinutes(expiryMinutes),
            Status = DashaPendingSignalStatus.AwaitingExpiry,
            TelegramMessageId = parsedSignal.TelegramMessageId,
            RawMessage = parsedSignal.RawMessage,
            CreatedAt = now,
            UpdatedAt = now
        };

        var signalId = await _dashaRepository.SavePendingSignalAsync(pendingSignal);

        _logger.LogInformation(
            "[DashaTrade] 🎯 Signal saved for selective martingale: Id={SignalId}, {Asset} {Direction} (expiry in {Expiry}min)",
            signalId, parsedSignal.Asset, direction, expiryMinutes);
        Console.WriteLine($"[DashaTrade] 🎯 SAVED: Signal #{signalId} - {parsedSignal.Asset} {direction} (expiry in {expiryMinutes}min)");
    }

    /// <summary>
    /// Parses timeframe string to minutes for DashaTrade signals.
    /// </summary>
    private static int ParseTimeframeToMinutes(string timeframe)
    {
        if (string.IsNullOrEmpty(timeframe)) return 15;

        var normalized = timeframe.ToUpperInvariant().Trim();

        // Handle formats: M5, M15, H1, H4, D1
        if (normalized.Length < 2) return 15;

        var unit = normalized[0];
        var numberStr = normalized.Substring(1);

        if (!int.TryParse(numberStr, out var number))
            return 15;

        return unit switch
        {
            'M' => number,           // Minutes
            'H' => number * 60,      // Hours
            'D' => number * 1440,    // Days
            _ => 15
        };
    }

    public override void Dispose()
    {
        Console.WriteLine("=== DISPOSING CLIENTS ===");
        _client1?.Dispose();
        _client2?.Dispose();
        base.Dispose();
    }
}
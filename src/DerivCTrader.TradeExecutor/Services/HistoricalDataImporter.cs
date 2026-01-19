using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// Imports historical price data from CSV files into MarketPriceHistory table.
/// Supports Yahoo Finance format and generic OHLC format.
/// </summary>
public class HistoricalDataImporter
{
    private readonly ILogger<HistoricalDataImporter> _logger;
    private readonly IBacktestRepository _backtestRepo;

    public HistoricalDataImporter(
        ILogger<HistoricalDataImporter> logger,
        IBacktestRepository backtestRepo)
    {
        _logger = logger;
        _backtestRepo = backtestRepo;
    }

    /// <summary>
    /// Import data from a Yahoo Finance CSV file.
    /// Expected format: Date,Open,High,Low,Close,Adj Close,Volume
    /// </summary>
    public async Task<int> ImportYahooFinanceCsvAsync(string filePath, string symbol)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogError("CSV file not found: {FilePath}", filePath);
            throw new FileNotFoundException("CSV file not found", filePath);
        }

        _logger.LogInformation("Importing Yahoo Finance CSV: {FilePath} for symbol {Symbol}", filePath, symbol);
        Console.WriteLine($"Importing {symbol} from {Path.GetFileName(filePath)}...");

        var candles = new List<MarketPriceCandle>();
        var lineNumber = 0;

        using var reader = new StreamReader(filePath);

        // Skip header
        await reader.ReadLineAsync();
        lineNumber++;

        while (!reader.EndOfStream)
        {
            lineNumber++;
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var parts = line.Split(',');
                if (parts.Length < 6) continue;

                // Parse date (Yahoo format: yyyy-MM-dd)
                if (!DateTime.TryParse(parts[0], out var date))
                {
                    _logger.LogWarning("Invalid date at line {Line}: {Value}", lineNumber, parts[0]);
                    continue;
                }

                // Parse prices
                if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var open) ||
                    !decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var high) ||
                    !decimal.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var low) ||
                    !decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var close))
                {
                    _logger.LogWarning("Invalid price data at line {Line}", lineNumber);
                    continue;
                }

                // Parse volume (optional)
                long? volume = null;
                if (parts.Length > 6 && long.TryParse(parts[6], out var vol))
                {
                    volume = vol;
                }

                candles.Add(new MarketPriceCandle
                {
                    Symbol = symbol,
                    TimeUtc = date.Date, // Daily data - use midnight UTC
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Volume = volume,
                    DataSource = "YahooFinance"
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing line {Line}", lineNumber);
            }
        }

        if (candles.Count > 0)
        {
            await _backtestRepo.BulkInsertCandlesAsync(candles);
            _logger.LogInformation("Imported {Count} candles for {Symbol}", candles.Count, symbol);
            Console.WriteLine($"  Imported {candles.Count} candles");
        }

        return candles.Count;
    }

    /// <summary>
    /// Import 1-minute intraday data from CSV.
    /// Expected format: DateTime,Open,High,Low,Close,Volume
    /// DateTime should be in UTC or specify timezone.
    /// </summary>
    public async Task<int> ImportIntradayCsvAsync(string filePath, string symbol, string dateTimeFormat = "yyyy-MM-dd HH:mm:ss")
    {
        if (!File.Exists(filePath))
        {
            _logger.LogError("CSV file not found: {FilePath}", filePath);
            throw new FileNotFoundException("CSV file not found", filePath);
        }

        _logger.LogInformation("Importing intraday CSV: {FilePath} for symbol {Symbol}", filePath, symbol);
        Console.WriteLine($"Importing {symbol} intraday data from {Path.GetFileName(filePath)}...");

        var candles = new List<MarketPriceCandle>();
        var lineNumber = 0;

        using var reader = new StreamReader(filePath);

        // Skip header
        await reader.ReadLineAsync();
        lineNumber++;

        while (!reader.EndOfStream)
        {
            lineNumber++;
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var parts = line.Split(',');
                if (parts.Length < 5) continue;

                // Parse datetime
                if (!DateTime.TryParseExact(parts[0], dateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dateTime))
                {
                    // Try generic parsing
                    if (!DateTime.TryParse(parts[0], out dateTime))
                    {
                        _logger.LogWarning("Invalid datetime at line {Line}: {Value}", lineNumber, parts[0]);
                        continue;
                    }
                }

                // Ensure UTC
                dateTime = DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);

                // Parse prices
                if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var open) ||
                    !decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var high) ||
                    !decimal.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var low) ||
                    !decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var close))
                {
                    _logger.LogWarning("Invalid price data at line {Line}", lineNumber);
                    continue;
                }

                // Parse volume (optional)
                long? volume = null;
                if (parts.Length > 5 && long.TryParse(parts[5], out var vol))
                {
                    volume = vol;
                }

                candles.Add(new MarketPriceCandle
                {
                    Symbol = symbol,
                    TimeUtc = dateTime,
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Volume = volume,
                    DataSource = "CSV"
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing line {Line}", lineNumber);
            }
        }

        if (candles.Count > 0)
        {
            // Insert in batches to avoid transaction timeout
            const int batchSize = 5000;
            var totalInserted = 0;

            for (var i = 0; i < candles.Count; i += batchSize)
            {
                var batch = candles.Skip(i).Take(batchSize);
                await _backtestRepo.BulkInsertCandlesAsync(batch);
                totalInserted += batch.Count();
                Console.WriteLine($"  Inserted {totalInserted}/{candles.Count}...");
            }

            _logger.LogInformation("Imported {Count} intraday candles for {Symbol}", candles.Count, symbol);
        }

        return candles.Count;
    }

    /// <summary>
    /// Check data coverage for backtest symbols.
    /// </summary>
    public async Task PrintDataCoverageAsync()
    {
        Console.WriteLine("\n--- Data Coverage ---");

        var symbols = new[] { "GS", "YM=F", "WS30" };

        foreach (var symbol in symbols)
        {
            var coverage = await _backtestRepo.GetDataCoverageAsync(symbol);

            if (coverage.Count > 0)
            {
                Console.WriteLine($"  {symbol}: {coverage.Earliest:yyyy-MM-dd} to {coverage.Latest:yyyy-MM-dd} ({coverage.Count:N0} candles)");
            }
            else
            {
                Console.WriteLine($"  {symbol}: NO DATA");
            }
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Import all required symbols from a directory containing CSV files.
    /// Expected filenames: GS.csv, YM=F.csv (or YM_F.csv), WS30.csv
    /// </summary>
    public async Task ImportFromDirectoryAsync(string directoryPath, bool isIntraday = false)
    {
        if (!Directory.Exists(directoryPath))
        {
            _logger.LogError("Directory not found: {DirectoryPath}", directoryPath);
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");
        }

        Console.WriteLine($"\nImporting from directory: {directoryPath}");

        var symbolMap = new Dictionary<string, string[]>
        {
            { "GS", new[] { "GS.csv", "gs.csv" } },
            { "YM=F", new[] { "YM=F.csv", "YM_F.csv", "YMF.csv", "ym=f.csv", "ym_f.csv" } },
            { "WS30", new[] { "WS30.csv", "ws30.csv", "WALLSTREET30.csv" } }
        };

        var totalImported = 0;

        foreach (var kvp in symbolMap)
        {
            var symbol = kvp.Key;
            var possibleFiles = kvp.Value;

            string? foundFile = null;
            foreach (var filename in possibleFiles)
            {
                var path = Path.Combine(directoryPath, filename);
                if (File.Exists(path))
                {
                    foundFile = path;
                    break;
                }
            }

            if (foundFile != null)
            {
                var count = isIntraday
                    ? await ImportIntradayCsvAsync(foundFile, symbol)
                    : await ImportYahooFinanceCsvAsync(foundFile, symbol);
                totalImported += count;
            }
            else
            {
                Console.WriteLine($"  {symbol}: No CSV file found");
            }
        }

        Console.WriteLine($"\nTotal imported: {totalImported} candles");
        await PrintDataCoverageAsync();
    }
}

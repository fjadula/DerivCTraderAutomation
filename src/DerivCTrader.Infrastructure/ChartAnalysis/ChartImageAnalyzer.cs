using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tesseract;

namespace DerivCTrader.Infrastructure.ChartAnalysis;

/// <summary>
/// Analyzes chart images from ChartSense provider to extract:
/// - Symbol/Asset (via OCR)
/// - Direction (Buy/Sell)
/// - Timeframe
/// - Pattern type
/// - Entry price (derived from line detection and Y-axis calibration)
/// </summary>
public class ChartImageAnalyzer : IChartImageAnalyzer
{
    private readonly ILogger<ChartImageAnalyzer> _logger;
    private readonly string _tessdataPath;

    // Known forex pairs and gold
    private static readonly string[] KnownSymbols =
    {
        "EURUSD", "GBPUSD", "USDJPY", "USDCHF", "AUDUSD", "USDCAD", "NZDUSD",
        "EURGBP", "EURJPY", "GBPJPY", "AUDJPY", "CADJPY", "CHFJPY", "NZDJPY",
        "EURAUD", "EURCHF", "EURCAD", "EURNZD", "GBPAUD", "GBPCAD", "GBPCHF",
        "GBPNZD", "AUDCAD", "AUDCHF", "AUDNZD", "CADCHF", "NZDCAD", "NZDCHF",
        "XAUUSD", "GOLD"
    };

    // Known timeframes
    private static readonly string[] KnownTimeframes =
    {
        "D1", "1D", "DAILY", "H4", "4H", "H2", "2H", "H1", "1H", "M30", "30M", "M15", "15M", "M5", "5M", "M1", "1M"
    };

    // Direction keywords
    private static readonly string[] BuyKeywords = { "BUY", "LONG", "BULLISH", "UP" };
    private static readonly string[] SellKeywords = { "SELL", "SHORT", "BEARISH", "DOWN" };

    // Pattern keywords
    private static readonly string[] PatternKeywords =
    {
        "WEDGE", "TRIANGLE", "CHANNEL", "TRENDLINE", "SUPPORT", "RESISTANCE",
        "BREAKOUT", "BREAKDOWN", "REVERSAL", "CONTINUATION", "FLAG", "PENNANT",
        "HEAD AND SHOULDERS", "DOUBLE TOP", "DOUBLE BOTTOM", "RISING", "FALLING"
    };

    public ChartImageAnalyzer(ILogger<ChartImageAnalyzer> logger, string? tessdataPath = null)
    {
        _logger = logger;
        _tessdataPath = tessdataPath ?? GetDefaultTessdataPath();
    }

    public async Task<ChartAnalysisResult> AnalyzeAsync(byte[] imageData)
    {
        var result = new ChartAnalysisResult();

        try
        {
            // 1. Extract text via OCR
            var extractedText = await ExtractTextAsync(imageData);
            _logger.LogDebug("OCR extracted text: {Text}", extractedText);

            // 2. Parse extracted text for trading info
            result.Symbol = ExtractSymbol(extractedText);
            result.Timeframe = ExtractTimeframe(extractedText);
            result.Direction = ExtractDirection(extractedText);
            result.PatternType = ExtractPattern(extractedText);

            // 3. Detect horizontal lines (support/resistance)
            var horizontalLines = await DetectHorizontalLinesAsync(imageData);

            if (horizontalLines.Count > 0)
            {
                result.DetectedLines = JsonSerializer.Serialize(horizontalLines);
            }

            // 4. Attempt Y-axis calibration and entry price derivation
            var calibration = await CalibrateYAxisAsync(imageData, extractedText);
            if (calibration != null)
            {
                result.CalibrationData = JsonSerializer.Serialize(calibration);

                // Derive entry price from detected lines
                if (horizontalLines.Count > 0 && calibration.PricePerPixel != 0)
                {
                    result.EntryPrice = DeriveEntryPrice(horizontalLines, calibration, result.Direction);
                    if (result.EntryPrice.HasValue)
                    {
                        var buffer = GetEntryBuffer(result.Symbol);
                        result.EntryZoneMin = result.EntryPrice.Value - buffer;
                        result.EntryZoneMax = result.EntryPrice.Value + buffer;
                    }
                }
            }

            // Determine success based on what we extracted
            result.Success = !string.IsNullOrEmpty(result.Symbol) || !string.IsNullOrEmpty(result.Direction);
            result.Confidence = CalculateConfidence(result);

            _logger.LogInformation(
                "Chart analysis complete: Symbol={Symbol}, Direction={Direction}, Timeframe={Timeframe}, Pattern={Pattern}, Entry={Entry}, Confidence={Confidence:P0}",
                result.Symbol, result.Direction, result.Timeframe, result.PatternType, result.EntryPrice, result.Confidence);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chart image analysis failed");
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    public async Task<string> ExtractTextAsync(byte[] imageData)
    {
        return await Task.Run(() =>
        {
            try
            {
                // Preprocess image for better OCR
                using var preprocessed = PreprocessImageForOcr(imageData);
                using var ms = new MemoryStream();
                preprocessed.SaveAsPng(ms);
                var processedBytes = ms.ToArray();

                // Run Tesseract OCR
                using var engine = new TesseractEngine(_tessdataPath, "eng", EngineMode.Default);
                using var pix = Pix.LoadFromMemory(processedBytes);
                using var page = engine.Process(pix);

                var text = page.GetText();
                return text?.Trim() ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OCR extraction failed, returning empty text");
                return string.Empty;
            }
        });
    }

    public async Task<List<int>> DetectHorizontalLinesAsync(byte[] imageData)
    {
        return await Task.Run(() =>
        {
            var detectedLines = new List<int>();

            try
            {
                using var image = Image.Load<Rgba32>(imageData);

                // Convert to grayscale and detect edges
                image.Mutate(x => x.Grayscale());

                var width = image.Width;
                var height = image.Height;

                // Simple horizontal line detection:
                // Look for rows where most pixels are similar (indicating a horizontal line)
                var lineThreshold = width * 0.3; // Line must span at least 30% of width
                var colorThreshold = 30; // Color difference threshold

                for (int y = 10; y < height - 10; y++)
                {
                    var consecutiveCount = 0;
                    Rgba32 prevPixel = default;

                    for (int x = (int)(width * 0.1); x < width * 0.9; x++) // Skip edges
                    {
                        var pixel = image[x, y];

                        // Check if pixel is part of a line (high contrast from background)
                        var intensity = (pixel.R + pixel.G + pixel.B) / 3;

                        // Detect dark lines on light background or light lines on dark background
                        if (intensity < 100 || intensity > 200)
                        {
                            if (prevPixel.R == 0 && prevPixel.G == 0 && prevPixel.B == 0)
                            {
                                prevPixel = pixel;
                                consecutiveCount = 1;
                            }
                            else
                            {
                                var diff = Math.Abs(pixel.R - prevPixel.R) +
                                          Math.Abs(pixel.G - prevPixel.G) +
                                          Math.Abs(pixel.B - prevPixel.B);

                                if (diff < colorThreshold)
                                {
                                    consecutiveCount++;
                                }
                                else
                                {
                                    if (consecutiveCount > lineThreshold)
                                    {
                                        detectedLines.Add(y);
                                    }
                                    consecutiveCount = 1;
                                    prevPixel = pixel;
                                }
                            }
                        }
                        else
                        {
                            if (consecutiveCount > lineThreshold)
                            {
                                detectedLines.Add(y);
                            }
                            consecutiveCount = 0;
                            prevPixel = default;
                        }
                    }

                    if (consecutiveCount > lineThreshold && !detectedLines.Contains(y))
                    {
                        detectedLines.Add(y);
                    }
                }

                // Remove duplicate/close lines (within 5 pixels)
                detectedLines = detectedLines
                    .OrderBy(l => l)
                    .Where((l, i) => i == 0 || l - detectedLines[i - 1] > 5)
                    .ToList();

                _logger.LogDebug("Detected {Count} horizontal lines at Y positions: {Lines}",
                    detectedLines.Count, string.Join(", ", detectedLines));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Horizontal line detection failed");
            }

            return detectedLines;
        });
    }

    private Image<Rgba32> PreprocessImageForOcr(byte[] imageData)
    {
        var image = Image.Load<Rgba32>(imageData);

        // Apply preprocessing for better OCR:
        // 1. Convert to grayscale
        // 2. Increase contrast
        // 3. Apply slight sharpening
        image.Mutate(x => x
            .Grayscale()
            .Contrast(1.2f)
            .GaussianSharpen(0.5f));

        return image;
    }

    private string? ExtractSymbol(string text)
    {
        var upperText = text.ToUpperInvariant();

        foreach (var symbol in KnownSymbols)
        {
            if (upperText.Contains(symbol))
            {
                return symbol == "GOLD" ? "XAUUSD" : symbol;
            }
        }

        // Try to find forex pair pattern (6 uppercase letters)
        var pairMatch = Regex.Match(upperText, @"\b([A-Z]{6})\b");
        if (pairMatch.Success)
        {
            var potential = pairMatch.Groups[1].Value;
            // Validate it looks like a currency pair
            if (KnownSymbols.Any(s => s.StartsWith(potential.Substring(0, 3)) ||
                                      s.EndsWith(potential.Substring(3))))
            {
                return potential;
            }
        }

        return null;
    }

    private string? ExtractTimeframe(string text)
    {
        var upperText = text.ToUpperInvariant();

        foreach (var tf in KnownTimeframes)
        {
            if (upperText.Contains(tf))
            {
                // Normalize timeframe format
                return tf.ToUpperInvariant() switch
                {
                    "1D" or "DAILY" => "D1",
                    "4H" => "H4",
                    "2H" => "H2",
                    "1H" => "H1",
                    "30M" => "M30",
                    "15M" => "M15",
                    "5M" => "M5",
                    "1M" => "M1",
                    _ => tf.ToUpperInvariant()
                };
            }
        }

        return null;
    }

    private string? ExtractDirection(string text)
    {
        var upperText = text.ToUpperInvariant();

        // Check for buy keywords
        if (BuyKeywords.Any(k => upperText.Contains(k)))
        {
            return "Buy";
        }

        // Check for sell keywords
        if (SellKeywords.Any(k => upperText.Contains(k)))
        {
            return "Sell";
        }

        // Check for arrow indicators
        if (upperText.Contains("↑") || upperText.Contains("▲"))
        {
            return "Buy";
        }
        if (upperText.Contains("↓") || upperText.Contains("▼"))
        {
            return "Sell";
        }

        return null;
    }

    private string? ExtractPattern(string text)
    {
        var upperText = text.ToUpperInvariant();

        foreach (var pattern in PatternKeywords)
        {
            if (upperText.Contains(pattern))
            {
                return pattern;
            }
        }

        return null;
    }

    private async Task<YAxisCalibration?> CalibrateYAxisAsync(byte[] imageData, string extractedText)
    {
        return await Task.Run(() =>
        {
            try
            {
                // Try to extract price values from OCR text on Y-axis
                var priceMatches = Regex.Matches(extractedText, @"\b(\d{1,5}\.?\d{0,5})\b");
                var prices = new List<(decimal Price, int YPosition)>();

                foreach (Match match in priceMatches)
                {
                    if (decimal.TryParse(match.Value, out var price) && price > 0)
                    {
                        // Estimate Y position (this is approximate)
                        // In a real implementation, we'd need to OCR with position info
                        prices.Add((price, 0));
                    }
                }

                if (prices.Count < 2)
                {
                    return null;
                }

                // Sort by price to find range
                var sortedPrices = prices.OrderByDescending(p => p.Price).ToList();
                var highPrice = sortedPrices.First().Price;
                var lowPrice = sortedPrices.Last().Price;

                // Estimate image height (standard chart dimensions)
                using var image = Image.Load<Rgba32>(imageData);
                var chartHeight = (int)(image.Height * 0.8); // Assume 80% is chart area

                return new YAxisCalibration
                {
                    HighPrice = highPrice,
                    LowPrice = lowPrice,
                    HighY = 50, // Top of chart area
                    LowY = chartHeight,
                    PricePerPixel = (highPrice - lowPrice) / (chartHeight - 50)
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Y-axis calibration failed");
                return null;
            }
        });
    }

    private decimal? DeriveEntryPrice(List<int> lines, YAxisCalibration calibration, string? direction)
    {
        if (lines.Count == 0 || calibration.PricePerPixel == 0)
        {
            return null;
        }

        // For Buy signals, entry is typically near the lowest support line
        // For Sell signals, entry is typically near the highest resistance line
        int targetY;
        if (direction?.Equals("Buy", StringComparison.OrdinalIgnoreCase) == true)
        {
            targetY = lines.Max(); // Lowest line (highest Y value)
        }
        else if (direction?.Equals("Sell", StringComparison.OrdinalIgnoreCase) == true)
        {
            targetY = lines.Min(); // Highest line (lowest Y value)
        }
        else
        {
            // Unknown direction - use middle line
            targetY = lines[lines.Count / 2];
        }

        // Convert Y position to price
        var priceOffset = (targetY - calibration.HighY) * calibration.PricePerPixel;
        var entryPrice = calibration.HighPrice - priceOffset;

        return Math.Round(entryPrice, 5);
    }

    private static decimal GetEntryBuffer(string? symbol)
    {
        if (string.IsNullOrEmpty(symbol))
            return 0.0005m;

        if (symbol.Contains("XAU", StringComparison.OrdinalIgnoreCase) ||
            symbol.Contains("GOLD", StringComparison.OrdinalIgnoreCase))
        {
            return 0.50m; // 50 cents for gold
        }

        if (symbol.EndsWith("JPY", StringComparison.OrdinalIgnoreCase))
        {
            return 0.05m; // 5 pips for JPY pairs
        }

        return 0.0005m; // 5 pips for standard pairs
    }

    private static double CalculateConfidence(ChartAnalysisResult result)
    {
        var score = 0.0;
        var factors = 0;

        if (!string.IsNullOrEmpty(result.Symbol)) { score += 0.3; factors++; }
        if (!string.IsNullOrEmpty(result.Direction)) { score += 0.3; factors++; }
        if (!string.IsNullOrEmpty(result.Timeframe)) { score += 0.15; factors++; }
        if (!string.IsNullOrEmpty(result.PatternType)) { score += 0.1; factors++; }
        if (result.EntryPrice.HasValue) { score += 0.15; factors++; }

        return factors > 0 ? score : 0.0;
    }

    private static string GetDefaultTessdataPath()
    {
        // Try common locations
        var possiblePaths = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tessdata"),
            @"C:\Program Files\Tesseract-OCR\tessdata",
            "/usr/share/tessdata"
        };

        foreach (var path in possiblePaths)
        {
            if (Directory.Exists(path))
            {
                return path;
            }
        }

        return "tessdata"; // Default fallback
    }
}

/// <summary>
/// Y-axis calibration data for mapping pixel positions to prices
/// </summary>
public class YAxisCalibration
{
    public decimal HighPrice { get; set; }
    public decimal LowPrice { get; set; }
    public int HighY { get; set; }
    public int LowY { get; set; }
    public decimal PricePerPixel { get; set; }
}

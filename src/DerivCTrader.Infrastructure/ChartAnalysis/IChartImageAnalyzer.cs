namespace DerivCTrader.Infrastructure.ChartAnalysis;

/// <summary>
/// Result of chart image analysis containing derived entry price and calibration data
/// </summary>
public class ChartAnalysisResult
{
    /// <summary>Derived entry price from chart structure lines</summary>
    public decimal? EntryPrice { get; set; }

    /// <summary>Lower bound of entry zone</summary>
    public decimal? EntryZoneMin { get; set; }

    /// <summary>Upper bound of entry zone</summary>
    public decimal? EntryZoneMax { get; set; }

    /// <summary>Extracted symbol/asset from image</summary>
    public string? Symbol { get; set; }

    /// <summary>Extracted timeframe from image</summary>
    public string? Timeframe { get; set; }

    /// <summary>Extracted direction (Buy/Sell) from image</summary>
    public string? Direction { get; set; }

    /// <summary>Extracted pattern type from image</summary>
    public string? PatternType { get; set; }

    /// <summary>JSON: Y-axis calibration parameters</summary>
    public string? CalibrationData { get; set; }

    /// <summary>JSON: Detected support/resistance line coordinates</summary>
    public string? DetectedLines { get; set; }

    /// <summary>Whether analysis was successful</summary>
    public bool Success { get; set; }

    /// <summary>Error message if analysis failed</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Confidence score (0.0 to 1.0)</summary>
    public double Confidence { get; set; }
}

/// <summary>
/// Interface for chart image analysis - OCR extraction, Y-axis calibration, line detection
/// </summary>
public interface IChartImageAnalyzer
{
    /// <summary>
    /// Analyze a chart image to extract trading information
    /// </summary>
    /// <param name="imageData">Raw image bytes</param>
    /// <returns>Analysis result with entry price, symbol, direction, etc.</returns>
    Task<ChartAnalysisResult> AnalyzeAsync(byte[] imageData);

    /// <summary>
    /// Extract text from image using OCR
    /// </summary>
    /// <param name="imageData">Raw image bytes</param>
    /// <returns>Extracted text</returns>
    Task<string> ExtractTextAsync(byte[] imageData);

    /// <summary>
    /// Detect horizontal lines in chart image (support/resistance levels)
    /// </summary>
    /// <param name="imageData">Raw image bytes</param>
    /// <returns>List of detected line Y-coordinates in pixels</returns>
    Task<List<int>> DetectHorizontalLinesAsync(byte[] imageData);
}

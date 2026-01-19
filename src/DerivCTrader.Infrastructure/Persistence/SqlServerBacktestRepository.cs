using Dapper;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Infrastructure.Persistence;

/// <summary>
/// SQL Server implementation of backtest repository
/// </summary>
public class SqlServerBacktestRepository : IBacktestRepository
{
    private readonly string _connectionString;
    private readonly ILogger<SqlServerBacktestRepository> _logger;

    public SqlServerBacktestRepository(IConfiguration configuration, ILogger<SqlServerBacktestRepository> logger)
    {
        _connectionString = configuration.GetConnectionString("ConnectionString")
            ?? throw new InvalidOperationException("ConnectionString not configured");
        _logger = logger;
    }

    // ===== MARKET PRICE HISTORY =====

    public async Task<MarketPriceCandle?> GetCandleAsync(string symbol, DateTime timeUtc)
    {
        const string sql = @"
            SELECT Id, Symbol, TimeUtc, [Open], High, Low, [Close], Volume, DataSource
            FROM MarketPriceHistory
            WHERE Symbol = @Symbol AND TimeUtc = @TimeUtc";

        await using var connection = new SqlConnection(_connectionString);
        return await connection.QueryFirstOrDefaultAsync<MarketPriceCandle>(sql, new { Symbol = symbol, TimeUtc = timeUtc });
    }

    public async Task<List<MarketPriceCandle>> GetCandlesAsync(string symbol, DateTime startUtc, DateTime endUtc)
    {
        const string sql = @"
            SELECT Id, Symbol, TimeUtc, [Open], High, Low, [Close], Volume, DataSource
            FROM MarketPriceHistory
            WHERE Symbol = @Symbol AND TimeUtc >= @StartUtc AND TimeUtc <= @EndUtc
            ORDER BY TimeUtc";

        await using var connection = new SqlConnection(_connectionString);
        var results = await connection.QueryAsync<MarketPriceCandle>(sql, new { Symbol = symbol, StartUtc = startUtc, EndUtc = endUtc });
        return results.ToList();
    }

    public async Task<decimal?> GetPriceAtTimeAsync(string symbol, DateTime timeUtc)
    {
        // Get exact match or closest candle at or after the target time (same day only)
        // This ensures we get the first available price on that day, not previous day's
        const string sql = @"
            SELECT TOP 1 [Close]
            FROM MarketPriceHistory
            WHERE Symbol = @Symbol
              AND CAST(TimeUtc AS DATE) = CAST(@TimeUtc AS DATE)
              AND TimeUtc >= @TimeUtc
            ORDER BY TimeUtc ASC";

        await using var connection = new SqlConnection(_connectionString);
        var result = await connection.QueryFirstOrDefaultAsync<decimal?>(sql, new { Symbol = symbol, TimeUtc = timeUtc });

        // If no candle at or after target time, try to get the closest one before on same day
        if (!result.HasValue)
        {
            const string sqlBefore = @"
                SELECT TOP 1 [Close]
                FROM MarketPriceHistory
                WHERE Symbol = @Symbol
                  AND CAST(TimeUtc AS DATE) = CAST(@TimeUtc AS DATE)
                  AND TimeUtc < @TimeUtc
                ORDER BY TimeUtc DESC";
            result = await connection.QueryFirstOrDefaultAsync<decimal?>(sqlBefore, new { Symbol = symbol, TimeUtc = timeUtc });
        }

        return result;
    }

    public async Task<decimal?> GetPreviousCloseForDateAsync(string symbol, DateTime tradeDate)
    {
        // Get the official market close (21:00 UTC) from the prior trading day
        // This is the correct "previous close" for calculating overnight gap
        var priorDay = tradeDate.AddDays(-1);

        // Skip weekends
        if (priorDay.DayOfWeek == DayOfWeek.Sunday)
            priorDay = priorDay.AddDays(-2); // Go back to Friday
        else if (priorDay.DayOfWeek == DayOfWeek.Saturday)
            priorDay = priorDay.AddDays(-1); // Go back to Friday

        // Look for the 21:00 UTC candle (market close) - prioritize exact match
        // If not found, fall back to closest candle around 21:00
        const string sql = @"
            SELECT TOP 1 [Close]
            FROM MarketPriceHistory
            WHERE Symbol = @Symbol
              AND CAST(TimeUtc AS DATE) = @PriorDate
              AND DATEPART(HOUR, TimeUtc) = 21
              AND DATEPART(MINUTE, TimeUtc) = 0
            ORDER BY TimeUtc DESC";

        await using var connection = new SqlConnection(_connectionString);
        var result = await connection.QueryFirstOrDefaultAsync<decimal?>(sql, new
        {
            Symbol = symbol,
            PriorDate = priorDay.Date
        });

        // Fallback: if no exact 21:00 match, get the latest candle from 20:59-21:01 window
        if (!result.HasValue)
        {
            const string fallbackSql = @"
                SELECT TOP 1 [Close]
                FROM MarketPriceHistory
                WHERE Symbol = @Symbol
                  AND CAST(TimeUtc AS DATE) = @PriorDate
                  AND TimeUtc >= DATEADD(MINUTE, -1, DATETIMEFROMPARTS(YEAR(@PriorDate), MONTH(@PriorDate), DAY(@PriorDate), 21, 0, 0, 0))
                  AND TimeUtc <= DATEADD(MINUTE, 1, DATETIMEFROMPARTS(YEAR(@PriorDate), MONTH(@PriorDate), DAY(@PriorDate), 21, 0, 0, 0))
                ORDER BY TimeUtc DESC";

            result = await connection.QueryFirstOrDefaultAsync<decimal?>(fallbackSql, new
            {
                Symbol = symbol,
                PriorDate = priorDay.Date
            });
        }

        return result;
    }

    public async Task BulkInsertCandlesAsync(IEnumerable<MarketPriceCandle> candles)
    {
        const string sql = @"
            INSERT INTO MarketPriceHistory (Symbol, TimeUtc, [Open], High, Low, [Close], Volume, DataSource)
            VALUES (@Symbol, @TimeUtc, @Open, @High, @Low, @Close, @Volume, @DataSource)";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var transaction = await connection.BeginTransactionAsync();
        try
        {
            foreach (var candle in candles)
            {
                await connection.ExecuteAsync(sql, candle, transaction);
            }
            await transaction.CommitAsync();
            _logger.LogInformation("Bulk inserted candles successfully");
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to bulk insert candles");
            throw;
        }
    }

    public async Task<(DateTime? Earliest, DateTime? Latest, int Count)> GetDataCoverageAsync(string symbol)
    {
        const string sql = @"
            SELECT MIN(TimeUtc) AS Earliest, MAX(TimeUtc) AS Latest, COUNT(*) AS [Count]
            FROM MarketPriceHistory
            WHERE Symbol = @Symbol";

        await using var connection = new SqlConnection(_connectionString);
        var result = await connection.QueryFirstOrDefaultAsync<(DateTime? Earliest, DateTime? Latest, int Count)>(sql, new { Symbol = symbol });
        return result;
    }

    // ===== BACKTEST RUNS =====

    public async Task<int> CreateBacktestRunAsync(BacktestRun run)
    {
        const string sql = @"
            INSERT INTO BacktestRuns (
                StrategyName, StartDate, EndDate,
                TotalTradingDays, TotalSignals, BuySignals, SellSignals, NoTradeSignals,
                BinaryTrades, BinaryWins, BinaryLosses, BinaryWinRate, BinaryTotalPnL,
                CFDTrades, CFDWins, CFDLosses, CFDWinRate, CFDTotalPnL,
                MaxDrawdown, MaxConsecutiveLosses, SharpeRatio,
                BinaryStakeUSD, CFDVolume, CFDStopLossPercent, CFDTakeProfitPercent,
                CreatedAt, Notes
            ) VALUES (
                @StrategyName, @StartDate, @EndDate,
                @TotalTradingDays, @TotalSignals, @BuySignals, @SellSignals, @NoTradeSignals,
                @BinaryTrades, @BinaryWins, @BinaryLosses, @BinaryWinRate, @BinaryTotalPnL,
                @CFDTrades, @CFDWins, @CFDLosses, @CFDWinRate, @CFDTotalPnL,
                @MaxDrawdown, @MaxConsecutiveLosses, @SharpeRatio,
                @BinaryStakeUSD, @CFDVolume, @CFDStopLossPercent, @CFDTakeProfitPercent,
                GETUTCDATE(), @Notes
            );
            SELECT SCOPE_IDENTITY();";

        await using var connection = new SqlConnection(_connectionString);
        var id = await connection.ExecuteScalarAsync<int>(sql, run);
        _logger.LogInformation("Created backtest run {RunId}", id);
        return id;
    }

    public async Task UpdateBacktestRunAsync(BacktestRun run)
    {
        const string sql = @"
            UPDATE BacktestRuns SET
                TotalTradingDays = @TotalTradingDays,
                TotalSignals = @TotalSignals,
                BuySignals = @BuySignals,
                SellSignals = @SellSignals,
                NoTradeSignals = @NoTradeSignals,
                BinaryTrades = @BinaryTrades,
                BinaryWins = @BinaryWins,
                BinaryLosses = @BinaryLosses,
                BinaryWinRate = @BinaryWinRate,
                BinaryTotalPnL = @BinaryTotalPnL,
                CFDTrades = @CFDTrades,
                CFDWins = @CFDWins,
                CFDLosses = @CFDLosses,
                CFDWinRate = @CFDWinRate,
                CFDTotalPnL = @CFDTotalPnL,
                MaxDrawdown = @MaxDrawdown,
                MaxConsecutiveLosses = @MaxConsecutiveLosses,
                SharpeRatio = @SharpeRatio,
                CompletedAt = @CompletedAt,
                Notes = @Notes
            WHERE RunId = @RunId";

        await using var connection = new SqlConnection(_connectionString);
        await connection.ExecuteAsync(sql, run);
        _logger.LogDebug("Updated backtest run {RunId}", run.RunId);
    }

    public async Task<BacktestRun?> GetBacktestRunAsync(int runId)
    {
        const string sql = @"
            SELECT * FROM BacktestRuns WHERE RunId = @RunId";

        await using var connection = new SqlConnection(_connectionString);
        return await connection.QueryFirstOrDefaultAsync<BacktestRun>(sql, new { RunId = runId });
    }

    public async Task<List<BacktestRun>> GetBacktestRunsAsync(string strategyName)
    {
        const string sql = @"
            SELECT * FROM BacktestRuns
            WHERE StrategyName = @StrategyName
            ORDER BY CreatedAt DESC";

        await using var connection = new SqlConnection(_connectionString);
        var results = await connection.QueryAsync<BacktestRun>(sql, new { StrategyName = strategyName });
        return results.ToList();
    }

    // ===== BACKTEST TRADES =====

    public async Task SaveBacktestTradeAsync(BacktestTrade trade)
    {
        const string sql = @"
            INSERT INTO BacktestTrades (
                RunId, TradeDate,
                GS_PreviousClose, GS_LatestPrice, GS_Direction, GS_Change,
                YM_PreviousClose, YM_LatestPrice, YM_Direction, YM_Change,
                FinalSignal, NoTradeReason,
                BinaryExpiry, BinaryEntryPrice, BinaryExitPrice, BinaryResult, BinaryPnL,
                CFDEntryPrice, CFDExitPrice, CFDStopLoss, CFDTakeProfit, CFDExitReason, CFDResult, CFDPnL, CFDExitTimeUtc,
                SnapshotTimeUtc, EntryTimeUtc
            ) VALUES (
                @RunId, @TradeDate,
                @GS_PreviousClose, @GS_LatestPrice, @GS_Direction, @GS_Change,
                @YM_PreviousClose, @YM_LatestPrice, @YM_Direction, @YM_Change,
                @FinalSignal, @NoTradeReason,
                @BinaryExpiry, @BinaryEntryPrice, @BinaryExitPrice, @BinaryResult, @BinaryPnL,
                @CFDEntryPrice, @CFDExitPrice, @CFDStopLoss, @CFDTakeProfit, @CFDExitReason, @CFDResult, @CFDPnL, @CFDExitTimeUtc,
                @SnapshotTimeUtc, @EntryTimeUtc
            )";

        await using var connection = new SqlConnection(_connectionString);
        await connection.ExecuteAsync(sql, trade);
    }

    public async Task BulkSaveBacktestTradesAsync(IEnumerable<BacktestTrade> trades)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var transaction = await connection.BeginTransactionAsync();
        try
        {
            foreach (var trade in trades)
            {
                const string sql = @"
                    INSERT INTO BacktestTrades (
                        RunId, TradeDate,
                        GS_PreviousClose, GS_LatestPrice, GS_Direction, GS_Change,
                        YM_PreviousClose, YM_LatestPrice, YM_Direction, YM_Change,
                        FinalSignal, NoTradeReason,
                        BinaryExpiry, BinaryEntryPrice, BinaryExitPrice, BinaryResult, BinaryPnL,
                        CFDEntryPrice, CFDExitPrice, CFDStopLoss, CFDTakeProfit, CFDExitReason, CFDResult, CFDPnL, CFDExitTimeUtc,
                        SnapshotTimeUtc, EntryTimeUtc
                    ) VALUES (
                        @RunId, @TradeDate,
                        @GS_PreviousClose, @GS_LatestPrice, @GS_Direction, @GS_Change,
                        @YM_PreviousClose, @YM_LatestPrice, @YM_Direction, @YM_Change,
                        @FinalSignal, @NoTradeReason,
                        @BinaryExpiry, @BinaryEntryPrice, @BinaryExitPrice, @BinaryResult, @BinaryPnL,
                        @CFDEntryPrice, @CFDExitPrice, @CFDStopLoss, @CFDTakeProfit, @CFDExitReason, @CFDResult, @CFDPnL, @CFDExitTimeUtc,
                        @SnapshotTimeUtc, @EntryTimeUtc
                    )";
                await connection.ExecuteAsync(sql, trade, transaction);
            }
            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to bulk save backtest trades");
            throw;
        }
    }

    public async Task<List<BacktestTrade>> GetBacktestTradesAsync(int runId)
    {
        const string sql = @"
            SELECT * FROM BacktestTrades
            WHERE RunId = @RunId
            ORDER BY TradeDate";

        await using var connection = new SqlConnection(_connectionString);
        var results = await connection.QueryAsync<BacktestTrade>(sql, new { RunId = runId });
        return results.ToList();
    }

    // ===== UTILITIES =====

    public async Task<List<DateTime>> GetTradingDaysAsync(DateTime startDate, DateTime endDate)
    {
        // Get all weekdays that are not US holidays
        const string sql = @"
            WITH DateRange AS (
                SELECT @StartDate AS DateValue
                UNION ALL
                SELECT DATEADD(DAY, 1, DateValue)
                FROM DateRange
                WHERE DateValue < @EndDate
            )
            SELECT DateValue
            FROM DateRange
            WHERE DATEPART(WEEKDAY, DateValue) NOT IN (1, 7)  -- Exclude Sunday (1) and Saturday (7)
              AND DateValue NOT IN (SELECT HolidayDate FROM USMarketHolidays)
            OPTION (MAXRECURSION 400)";

        await using var connection = new SqlConnection(_connectionString);
        var results = await connection.QueryAsync<DateTime>(sql, new { StartDate = startDate.Date, EndDate = endDate.Date });
        return results.ToList();
    }
}

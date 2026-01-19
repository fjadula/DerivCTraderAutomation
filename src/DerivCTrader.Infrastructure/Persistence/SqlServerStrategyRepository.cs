using Dapper;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Infrastructure.Persistence;

/// <summary>
/// SQL Server implementation of strategy repository
/// </summary>
public class SqlServerStrategyRepository : IStrategyRepository
{
    private readonly string _connectionString;
    private readonly ILogger<SqlServerStrategyRepository> _logger;

    public SqlServerStrategyRepository(IConfiguration configuration, ILogger<SqlServerStrategyRepository> logger)
    {
        _connectionString = configuration.GetConnectionString("ConnectionString")
            ?? throw new InvalidOperationException("ConnectionString not configured");
        _logger = logger;
    }

    // ===== STRATEGY CONTROL =====

    public async Task<StrategyControl?> GetStrategyControlAsync(string strategyName)
    {
        const string sql = @"
            SELECT Id, StrategyName, IsEnabled, ExecuteCFD, ExecuteBinary, ExecuteMT5, DryRun, CreatedAt, UpdatedAt
            FROM StrategyControl
            WHERE StrategyName = @StrategyName";

        await using var connection = new SqlConnection(_connectionString);
        return await connection.QueryFirstOrDefaultAsync<StrategyControl>(sql, new { StrategyName = strategyName });
    }

    public async Task UpdateStrategyControlAsync(StrategyControl control)
    {
        const string sql = @"
            UPDATE StrategyControl
            SET IsEnabled = @IsEnabled,
                ExecuteCFD = @ExecuteCFD,
                ExecuteBinary = @ExecuteBinary,
                ExecuteMT5 = @ExecuteMT5,
                DryRun = @DryRun,
                UpdatedAt = GETUTCDATE()
            WHERE StrategyName = @StrategyName";

        await using var connection = new SqlConnection(_connectionString);
        await connection.ExecuteAsync(sql, control);
    }

    // ===== STRATEGY PARAMETERS =====

    public async Task<List<StrategyParameter>> GetStrategyParametersAsync(string strategyName)
    {
        const string sql = @"
            SELECT Id, StrategyName, ParameterName, ParameterValue, Description, CreatedAt, UpdatedAt
            FROM StrategyParameters
            WHERE StrategyName = @StrategyName";

        await using var connection = new SqlConnection(_connectionString);
        var result = await connection.QueryAsync<StrategyParameter>(sql, new { StrategyName = strategyName });
        return result.ToList();
    }

    public async Task<string?> GetStrategyParameterAsync(string strategyName, string parameterName)
    {
        const string sql = @"
            SELECT ParameterValue
            FROM StrategyParameters
            WHERE StrategyName = @StrategyName AND ParameterName = @ParameterName";

        await using var connection = new SqlConnection(_connectionString);
        return await connection.QueryFirstOrDefaultAsync<string>(sql,
            new { StrategyName = strategyName, ParameterName = parameterName });
    }

    public async Task UpdateStrategyParameterAsync(string strategyName, string parameterName, string value)
    {
        const string sql = @"
            UPDATE StrategyParameters
            SET ParameterValue = @Value,
                UpdatedAt = GETUTCDATE()
            WHERE StrategyName = @StrategyName AND ParameterName = @ParameterName";

        await using var connection = new SqlConnection(_connectionString);
        await connection.ExecuteAsync(sql,
            new { StrategyName = strategyName, ParameterName = parameterName, Value = value });
    }

    // ===== DOW OPEN GS SIGNALS =====

    public async Task<int> SaveDowOpenGSSignalAsync(DowOpenGSSignal signal)
    {
        const string sql = @"
            INSERT INTO DowOpenGSSignals (
                TradeDate,
                GS_PreviousClose, GS_LatestPrice, GS_Direction, GS_Change,
                YM_PreviousClose, YM_LatestPrice, YM_Direction, YM_Change,
                FinalSignal, BinaryExpiry, NoTradeReason,
                WasDryRun, CFDExecuted, BinaryExecuted,
                CFDOrderId, BinaryContractId, CFDEntryPrice, CFDStopLoss, CFDTakeProfit,
                SnapshotAt, ExecutedAt, ErrorMessage
            )
            OUTPUT INSERTED.SignalId
            VALUES (
                @TradeDate,
                @GS_PreviousClose, @GS_LatestPrice, @GS_Direction, @GS_Change,
                @YM_PreviousClose, @YM_LatestPrice, @YM_Direction, @YM_Change,
                @FinalSignal, @BinaryExpiry, @NoTradeReason,
                @WasDryRun, @CFDExecuted, @BinaryExecuted,
                @CFDOrderId, @BinaryContractId, @CFDEntryPrice, @CFDStopLoss, @CFDTakeProfit,
                @SnapshotAt, @ExecutedAt, @ErrorMessage
            )";

        await using var connection = new SqlConnection(_connectionString);
        return await connection.QuerySingleAsync<int>(sql, signal);
    }

    public async Task<DowOpenGSSignal?> GetDowOpenGSSignalByDateAsync(DateTime tradeDate)
    {
        const string sql = @"
            SELECT *
            FROM DowOpenGSSignals
            WHERE TradeDate = @TradeDate";

        await using var connection = new SqlConnection(_connectionString);
        return await connection.QueryFirstOrDefaultAsync<DowOpenGSSignal>(sql,
            new { TradeDate = tradeDate.Date });
    }

    public async Task<List<DowOpenGSSignal>> GetRecentDowOpenGSSignalsAsync(int count = 10)
    {
        const string sql = @"
            SELECT TOP (@Count) *
            FROM DowOpenGSSignals
            ORDER BY TradeDate DESC";

        await using var connection = new SqlConnection(_connectionString);
        var result = await connection.QueryAsync<DowOpenGSSignal>(sql, new { Count = count });
        return result.ToList();
    }

    public async Task UpdateDowOpenGSSignalExecutionAsync(int signalId, string? cfdOrderId, string? binaryContractId,
        decimal? cfdEntryPrice, decimal? cfdStopLoss, decimal? cfdTakeProfit, string? errorMessage)
    {
        const string sql = @"
            UPDATE DowOpenGSSignals
            SET CFDOrderId = @CFDOrderId,
                BinaryContractId = @BinaryContractId,
                CFDEntryPrice = @CFDEntryPrice,
                CFDStopLoss = @CFDStopLoss,
                CFDTakeProfit = @CFDTakeProfit,
                CFDExecuted = CASE WHEN @CFDOrderId IS NOT NULL THEN 1 ELSE CFDExecuted END,
                BinaryExecuted = CASE WHEN @BinaryContractId IS NOT NULL THEN 1 ELSE BinaryExecuted END,
                ExecutedAt = GETUTCDATE(),
                ErrorMessage = @ErrorMessage
            WHERE SignalId = @SignalId";

        await using var connection = new SqlConnection(_connectionString);
        await connection.ExecuteAsync(sql, new
        {
            SignalId = signalId,
            CFDOrderId = cfdOrderId,
            BinaryContractId = binaryContractId,
            CFDEntryPrice = cfdEntryPrice,
            CFDStopLoss = cfdStopLoss,
            CFDTakeProfit = cfdTakeProfit,
            ErrorMessage = errorMessage
        });
    }

    // ===== MARKET PREVIOUS CLOSE =====

    public async Task<MarketPreviousClose?> GetPreviousCloseAsync(string symbol, DateTime closeDate)
    {
        const string sql = @"
            SELECT Id, Symbol, PreviousClose, CloseDate, CachedAt, DataSource
            FROM MarketPreviousClose
            WHERE Symbol = @Symbol AND CloseDate = @CloseDate";

        await using var connection = new SqlConnection(_connectionString);
        return await connection.QueryFirstOrDefaultAsync<MarketPreviousClose>(sql,
            new { Symbol = symbol, CloseDate = closeDate.Date });
    }

    public async Task<MarketPreviousClose?> GetLatestPreviousCloseAsync(string symbol)
    {
        const string sql = @"
            SELECT TOP 1 Id, Symbol, PreviousClose, CloseDate, CachedAt, DataSource
            FROM MarketPreviousClose
            WHERE Symbol = @Symbol
            ORDER BY CloseDate DESC";

        await using var connection = new SqlConnection(_connectionString);
        return await connection.QueryFirstOrDefaultAsync<MarketPreviousClose>(sql, new { Symbol = symbol });
    }

    public async Task UpsertPreviousCloseAsync(MarketPreviousClose previousClose)
    {
        const string sql = @"
            MERGE MarketPreviousClose AS target
            USING (SELECT @Symbol AS Symbol, @CloseDate AS CloseDate) AS source
            ON target.Symbol = source.Symbol AND target.CloseDate = source.CloseDate
            WHEN MATCHED THEN
                UPDATE SET PreviousClose = @PreviousClose,
                           CachedAt = GETUTCDATE(),
                           DataSource = @DataSource
            WHEN NOT MATCHED THEN
                INSERT (Symbol, PreviousClose, CloseDate, DataSource)
                VALUES (@Symbol, @PreviousClose, @CloseDate, @DataSource);";

        await using var connection = new SqlConnection(_connectionString);
        await connection.ExecuteAsync(sql, new
        {
            previousClose.Symbol,
            previousClose.PreviousClose,
            CloseDate = previousClose.CloseDate.Date,
            previousClose.DataSource
        });

        _logger.LogDebug("Upserted previous close: {Symbol} = {Price} on {Date}",
            previousClose.Symbol, previousClose.PreviousClose, previousClose.CloseDate.Date);
    }

    // ===== US MARKET HOLIDAYS =====

    public async Task<bool> IsUSMarketHolidayAsync(DateTime date)
    {
        const string sql = @"
            SELECT COUNT(1)
            FROM USMarketHolidays
            WHERE HolidayDate = @Date";

        await using var connection = new SqlConnection(_connectionString);
        var count = await connection.QuerySingleAsync<int>(sql, new { Date = date.Date });
        return count > 0;
    }

    public async Task<List<DateTime>> GetUSMarketHolidaysAsync(int year)
    {
        const string sql = @"
            SELECT HolidayDate
            FROM USMarketHolidays
            WHERE YEAR(HolidayDate) = @Year
            ORDER BY HolidayDate";

        await using var connection = new SqlConnection(_connectionString);
        var result = await connection.QueryAsync<DateTime>(sql, new { Year = year });
        return result.ToList();
    }
}

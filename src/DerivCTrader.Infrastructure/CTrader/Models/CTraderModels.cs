using DerivCTrader.Domain.Enums;

namespace DerivCTrader.Infrastructure.CTrader.Models;

/// <summary>
/// Generic cTrader message wrapper
/// </summary>
/*public class CTraderMessage
{
    public int PayloadType { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}*/

/// <summary>
/// Result of an order placement operation
/// </summary>
public class CTraderOrderResult
{
    public bool Success { get; set; }
    public long? OrderId { get; set; }
    public long? PositionId { get; set; }
    public bool? SltpApplied { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorCode { get; set; }
    public double? ExecutedPrice { get; set; }
    public double? ExecutedVolume { get; set; }
    public DateTime ExecutedAt { get; set; }
}

/// <summary>
/// Represents a cTrader position
/// </summary>
public class CTraderPosition
{
    public long PositionId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public TradeDirection Direction { get; set; }
    public double Volume { get; set; }
    public double EntryPrice { get; set; }
    public double? StopLoss { get; set; }
    public double? TakeProfit { get; set; }
    public double CurrentPrice { get; set; }
    public double Profit { get; set; }
    public double ProfitInPips { get; set; }
    public string? Label { get; set; }
    public string? Comment { get; set; }
    public DateTime OpenedAt { get; set; }
    public PositionStatus Status { get; set; }
}

/// <summary>
/// Position status
/// </summary>
public enum PositionStatus
{
    Open,
    Closed,
    PartiallyFilled
}

/// <summary>
/// cTrader order type
/// </summary>
public enum CTraderOrderType
{
    Market = 1,
    Limit = 2,
    Stop = 3,
    StopLimit = 4
}

/// <summary>
/// cTrader time in force
/// </summary>
public enum CTraderTimeInForce
{
    GoodTillCancel = 1,
    ImmediateOrCancel = 2,
    FillOrKill = 3,
    GoodTillDate = 4
}

/// <summary>
/// Proto message types (cTrader Open API payload types)
/// </summary>
public static class ProtoPayloadType
{
    // Heartbeat
    public const int HEARTBEAT_EVENT = 51;

    // Application authentication
    public const int PROTO_OA_APPLICATION_AUTH_REQ = 2100;
    public const int PROTO_OA_APPLICATION_AUTH_RES = 2101;

    // Account authentication
    public const int PROTO_OA_ACCOUNT_AUTH_REQ = 2102;
    public const int PROTO_OA_ACCOUNT_AUTH_RES = 2103;

    // Version
    public const int PROTO_OA_VERSION_REQ = 2104;
    public const int PROTO_OA_VERSION_RES = 2105;

    // Orders
    public const int PROTO_OA_NEW_ORDER_REQ = 2106;
    public const int PROTO_OA_TRAILING_SL_CHANGED_EVENT = 2107;
    public const int PROTO_OA_CANCEL_ORDER_REQ = 2108;
    public const int PROTO_OA_AMEND_ORDER_REQ = 2109;
    public const int PROTO_OA_AMEND_POSITION_SLTP_REQ = 2110;
    public const int PROTO_OA_CLOSE_POSITION_REQ = 2111;

    // Execution events
    public const int PROTO_OA_EXECUTION_EVENT = 2122;

    // Symbol info
    public const int PROTO_OA_SYMBOL_BY_ID_REQ = 2112;
    public const int PROTO_OA_SYMBOL_BY_ID_RES = 2113;
    public const int PROTO_OA_SYMBOLS_LIST_REQ = 2114;
    public const int PROTO_OA_SYMBOLS_LIST_RES = 2115;

    // Account info
    public const int PROTO_OA_TRADER_REQ = 2116;
    public const int PROTO_OA_TRADER_RES = 2117;
    public const int PROTO_OA_RECONCILE_REQ = 2118;
    public const int PROTO_OA_RECONCILE_RES = 2119;

    // Errors
    public const int PROTO_OA_ERROR_RES = 2142;
}

/// <summary>
/// Configuration for cTrader connection
/// </summary>
public class CTraderConfig
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string Environment { get; set; } = "Demo";
    public long DemoAccountId { get; set; }
    public long LiveAccountId { get; set; }
    public double DefaultLotSize { get; set; } = 0.2;
    public string WebSocketUrl { get; set; } = "wss://demo.ctraderapi.com";
    public int HeartbeatIntervalSeconds { get; set; } = 25;
    public int ConnectionTimeoutSeconds { get; set; } = 30;
    public int MessageTimeoutSeconds { get; set; } = 10;
}
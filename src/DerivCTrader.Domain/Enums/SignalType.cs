namespace DerivCTrader.Domain.Enums;

public enum SignalType
{
    Text,
    Image,
    PureBinary,  // For instant binary execution (VIP CHANNEL)
    MarketExecution  // For instant market execution (no pending order, no binary for Boom/Crash)
}

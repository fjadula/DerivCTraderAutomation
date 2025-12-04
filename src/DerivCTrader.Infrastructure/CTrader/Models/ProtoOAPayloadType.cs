namespace DerivCTrader.Infrastructure.CTrader.Models;

public enum ProtoOAPayloadType
{
    ProtoOaApplicationAuthReq = 2100,
    ProtoOaApplicationAuthRes = 2101,
    ProtoOaAccountAuthReq = 2102,
    ProtoOaAccountAuthRes = 2103,
    ProtoOaNewOrderReq = 2126,
    ProtoOaNewOrderRes = 2127,
    ProtoOaCancelOrderReq = 2128,
    ProtoOaExecutionEvent = 2132,
    ProtoOaGetSymbolsReq = 2124,
    ProtoOaGetSymbolsRes = 2125,
    ProtoOaReconcileReq = 2134,
    ProtoOaReconcileRes = 2135,
    ProtoHeartbeatEvent = 51
}
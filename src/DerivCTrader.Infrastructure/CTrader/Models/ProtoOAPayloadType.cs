namespace DerivCTrader.Infrastructure.CTrader.Models;

public enum ProtoOAPayloadType
{
    ProtoOaApplicationAuthReq = 2100,
    ProtoOaApplicationAuthRes = 2101,
    ProtoOaAccountAuthReq = 2102,
    ProtoOaAccountAuthRes = 2103,
    ProtoOaSymbolsListReq = 2114,
    ProtoOaSymbolsListRes = 2115,
    ProtoOaGetAccountListByAccessTokenReq = 2148,
    ProtoOaGetAccountListByAccessTokenRes = 2149,
    // cTrader server sends error responses as PayloadType=2132 in our environment
    ProtoOaErrorRes = 2132,
    ProtoOaNewOrderReq = 2106, // CORRECTED from 2126 (which was ProtoOaExecutionEvent)
    ProtoOaNewOrderRes = 2107,  // CORRECTED (previously was wrong sequence)
    ProtoOaCancelOrderReq = 2108, // CORRECTED
    ProtoOaClosePositionReq = 2111,
    ProtoOaExecutionEvent = 2126, // CORRECTED (was previously assigned to NewOrderReq)
    // Market data subscriptions
    ProtoOaSubscribeSpotsReq = 2120,
    ProtoOaSubscribeSpotsRes = 2121,
    ProtoOaUnsubscribeSpotsReq = 2122,
    ProtoOaUnsubscribeSpotsRes = 2123,
    ProtoOaGetSymbolsReq = 2124,
    ProtoOaGetSymbolsRes = 2125,
    ProtoOaReconcileReq = 2134,
    ProtoOaReconcileRes = 2135,
    ProtoOaSpotEvent = 2136,
    ProtoHeartbeatEvent = 51
}
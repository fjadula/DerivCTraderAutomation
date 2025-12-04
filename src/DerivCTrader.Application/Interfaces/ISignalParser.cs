using DerivCTrader.Domain.Entities;

namespace DerivCTrader.Application.Interfaces;

public interface ISignalParser
{
    Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null);
    bool CanParse(string providerChannelId);
}

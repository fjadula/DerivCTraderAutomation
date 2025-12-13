namespace DerivCTrader.Application.Interfaces;

public interface ITelegramNotifier
{
    Task SendTradeMessageAsync(string message, CancellationToken cancellationToken = default);
}

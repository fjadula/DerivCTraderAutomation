namespace DerivCTrader.Application.Interfaces;

public interface ITelegramNotifier
{
    /// <summary>
    /// Send a message (fire and forget, no message_id returned)
    /// </summary>
    Task SendTradeMessageAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a message and return the message_id for threading
    /// </summary>
    /// <param name="message">Message text to send</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The message_id of the sent message, or null if send failed</returns>
    Task<int?> SendTradeMessageWithIdAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a message as a reply to a previous message (threading)
    /// </summary>
    /// <param name="message">Message text to send</param>
    /// <param name="replyToMessageId">Telegram message_id to reply to (threading)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The message_id of the sent message, or null if send failed</returns>
    Task<int?> SendTradeMessageAsync(string message, int replyToMessageId, CancellationToken cancellationToken = default);
}

using System;
using System.Threading;
using System.Threading.Tasks;
using DerivCTrader.Infrastructure.Trading;

namespace DerivCTrader.Infrastructure.Deriv
{
    /// <summary>
    /// Utility for probing Deriv price for a symbol, used to classify LIMIT vs STOP for synthetics.
    /// </summary>
    public interface IDerivTickProvider
    {
        event EventHandler<DerivTickEventArgs> TickReceived;
        Task<string> SubscribeTickAsync(string symbol);
        Task UnsubscribeTickAsync(string subscriptionId);
    }

    public class DerivPriceProbe : IAsyncDisposable
    {
        private readonly IDerivTickProvider _client;
        private readonly string _symbol;
        private readonly SemaphoreSlim _semaphore;
        private readonly int _timeoutMs;
        private decimal? _lastPrice;
        private bool _subscribed;
        private string? _subscriptionId;

        public DerivPriceProbe(IDerivTickProvider client, string symbol, SemaphoreSlim semaphore, int timeoutMs = 2000)
        {
            _client = client;
            _symbol = symbol;
            _semaphore = semaphore;
            _timeoutMs = timeoutMs;
        }

        public async Task<decimal?> ProbeAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                var tcs = new TaskCompletionSource<decimal?>();
                void Handler(object? sender, DerivTickEventArgs e)
                {
                    if (e.Symbol == _symbol && e.Price > 0)
                    {
                        _lastPrice = e.Price;
                        tcs.TrySetResult(e.Price);
                    }
                }

                _client.TickReceived += Handler;
                _subscriptionId = await _client.SubscribeTickAsync(_symbol);
                _subscribed = true;

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(_timeoutMs));
                _client.TickReceived -= Handler;
                if (_subscribed && _subscriptionId != null)
                {
                    await _client.UnsubscribeTickAsync(_subscriptionId);
                    _subscribed = false;
                }
                return completed == tcs.Task ? tcs.Task.Result : null;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_subscribed && _subscriptionId != null)
            {
                await _client.UnsubscribeTickAsync(_subscriptionId);
                _subscribed = false;
            }
        }
    }

    public class DerivTickEventArgs : EventArgs
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal Price { get; set; }
    }
}

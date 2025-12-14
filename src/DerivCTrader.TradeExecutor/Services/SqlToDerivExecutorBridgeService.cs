using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor.Services
{
    /// <summary>
    /// Bridges SignalSqlNotificationService.SignalChanged to DerivBinaryExecutorService.WakeUp
    /// </summary>
    public class SqlToDerivExecutorBridgeService : IHostedService
    {
        private readonly SignalSqlNotificationService _sqlNotificationService;
        private readonly DerivBinaryExecutorService _derivExecutorService;
        private readonly ILogger<SqlToDerivExecutorBridgeService> _logger;

        public SqlToDerivExecutorBridgeService(
            SignalSqlNotificationService sqlNotificationService,
            DerivBinaryExecutorService derivExecutorService,
            ILogger<SqlToDerivExecutorBridgeService> logger)
        {
            _sqlNotificationService = sqlNotificationService;
            _derivExecutorService = derivExecutorService;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _sqlNotificationService.SignalChanged += OnSignalChanged;
            _logger.LogInformation("SqlToDerivExecutorBridgeService started: SQL notifications will wake DerivBinaryExecutorService");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _sqlNotificationService.SignalChanged -= OnSignalChanged;
            return Task.CompletedTask;
        }

        private void OnSignalChanged()
        {
            var now = DateTime.UtcNow.ToString("O");
            _logger.LogInformation("[TIMING] {Now} SQL notification received: waking DerivBinaryExecutorService", now);
            _derivExecutorService.WakeUp();
        }
    }
}

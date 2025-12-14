using System;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor.Services
{
    public class SignalSqlNotificationService : IDisposable
    {
        private readonly string _connectionString;
        private readonly ILogger<SignalSqlNotificationService> _logger;
        private SqlDependency _dependency;
        private SqlConnection _connection;
        private SqlCommand _command;
        private bool _disposed;

        public event Action SignalChanged;

        public SignalSqlNotificationService(string connectionString, ILogger<SignalSqlNotificationService> logger)
        {
            _connectionString = connectionString;
            _logger = logger;
            SqlDependency.Start(_connectionString);
        }

        public void StartListening()
        {
            _connection = new SqlConnection(_connectionString);
            _command = new SqlCommand(
                "SELECT SignalId FROM dbo.ParsedSignalsQueue WHERE Processed = 0", _connection);
            _dependency = new SqlDependency(_command);
            _dependency.OnChange += OnSignalChanged;
            _connection.Open();
            using var reader = _command.ExecuteReader();
        }

        private void OnSignalChanged(object sender, SqlNotificationEventArgs e)
        {
            var now = DateTime.UtcNow.ToString("O");
            _logger.LogInformation("[TIMING] {Now} SQL Dependency notification received: {Type} {Info}", now, e.Type, e.Info);
            SignalChanged?.Invoke();
            // Re-register for next notification
            _dependency.OnChange -= OnSignalChanged;
            _connection.Close();
            StartListening();
        }

        public void Dispose()
        {
            if (_disposed) return;
            SqlDependency.Stop(_connectionString);
            _connection?.Dispose();
            _command?.Dispose();
            _disposed = true;
        }
    }
}

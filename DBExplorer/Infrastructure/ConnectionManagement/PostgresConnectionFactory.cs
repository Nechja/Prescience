using Npgsql;
using System;
using System.Threading.Tasks;

namespace DBExplorer.Infrastructure.ConnectionManagement
{
    public class PostgresConnectionFactory : IDbConnectionFactory
    {
        private readonly string _connectionString;

        public PostgresConnectionFactory(string connectionString)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        public async Task<NpgsqlConnection> CreateConnectionAsync()
        {
            var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            return connection;
        }
    }
}

using Npgsql;
using System.Threading.Tasks;

namespace DBExplorer.Infrastructure.ConnectionManagement
{
    public interface IDbConnectionFactory
    {
        Task<NpgsqlConnection> CreateConnectionAsync();
    }
}

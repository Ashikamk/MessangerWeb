// Services/MySqlConnectionService.cs
using MySql.Data.MySqlClient;
using System.Data;
using Microsoft.Extensions.Configuration;
namespace MessangerWeb.Services
{
    public class MySqlConnectionService
    {
        private readonly string _connectionString;
        public MySqlConnectionService(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }
        public async Task<MySqlConnection> GetConnectionAsync()
        {
            var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            return connection;
        }
    }
}
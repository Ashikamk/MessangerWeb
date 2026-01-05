using Npgsql;
using System;

class Program
{
    static void Main()
    {
        var connString = "Host=localhost;Port=5432;Database=messengerdb_kwmb;Username=postgres;Password=admin123";
        
        try
        {
            Console.WriteLine("Testing connection...");
            Console.WriteLine($"Connection string: {connString}");
            
            using (var conn = new NpgsqlConnection(connString))
            {
                conn.Open();
                Console.WriteLine("✓ Connection successful!");
                Console.WriteLine($"PostgreSQL version: {conn.PostgreSqlVersion}");
                
                // Test a simple query
                using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM students", conn))
                {
                    var count = cmd.ExecuteScalar();
                    Console.WriteLine($"✓ Found {count} students in database");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Connection failed!");
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
        
        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }
}

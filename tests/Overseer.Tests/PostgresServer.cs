using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Overseer.Tests.PostgresServer))]

namespace Overseer.Tests;

/// <summary>One real PostgreSQL for the whole test run; each test gets its own empty database.</summary>
public sealed class PostgresServer : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async Task<string> CreateEmptyDatabaseAsync()
    {
        var name = $"overseer_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

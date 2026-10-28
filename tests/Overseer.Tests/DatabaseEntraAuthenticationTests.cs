using Azure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Overseer.Persistence;

namespace Overseer.Tests;

/// <summary>
/// In Azure, Overseer logs in to PostgreSQL as its Managed Identity: the password is a Microsoft Entra token. Here a role
/// whose password is a fake token stands in for Azure's PostgreSQL, which checks the token the same way.
/// </summary>
public class DatabaseEntraAuthenticationTests(PostgresServer postgres)
{
    private const string Token = "eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.fake-entra-token";

    [Fact]
    public async Task With_Entra_authentication_Overseer_logs_in_with_a_token_for_PostgreSQL_instead_of_a_password()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(await postgres.CreateEmptyDatabaseAsync());
        var role = $"identity_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(connectionString.ConnectionString))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand($"CREATE ROLE \"{role}\" LOGIN PASSWORD '{Token}'", admin);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        connectionString.Username = role;
        connectionString.Password = null;
        var credential = new FakeTokenCredential(Token);

        await using var services = new ServiceCollection()
            .AddDatabase(Settings(new()
            {
                ["ConnectionStrings:Overseer"] = connectionString.ConnectionString,
                ["Database:EntraAuthentication"] = "true",
            }), credential)
            .BuildServiceProvider();
        await using var database = await services.GetRequiredService<IDbContextFactory<OverseerDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(role, await database.Database.SqlQueryRaw<string>("SELECT current_user AS \"Value\"")
            .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["https://ossrdbms-aad.database.windows.net/.default"], credential.Scopes.Distinct());
    }

    [Fact]
    public async Task Without_Entra_authentication_Overseer_logs_in_with_the_password_in_the_connection_string()
    {
        var credential = new FakeTokenCredential(Token);

        await using var services = new ServiceCollection()
            .AddDatabase(Settings(new() { ["ConnectionStrings:Overseer"] = await postgres.CreateEmptyDatabaseAsync() }), credential)
            .BuildServiceProvider();
        await using var database = await services.GetRequiredService<IDbContextFactory<OverseerDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);

        Assert.True(await database.Database.CanConnectAsync(TestContext.Current.CancellationToken));
        Assert.Empty(credential.Scopes);
    }

    private static IConfiguration Settings(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private sealed class FakeTokenCredential(string token) : TokenCredential
    {
        public List<string> Scopes { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes.AddRange(requestContext.Scopes);
            return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}

using Azure.Core;
using Microsoft.EntityFrameworkCore;

namespace Overseer.Persistence;

/// <summary>
/// How <c>serve</c> reaches PostgreSQL: the configured connection string, logging in with its password or, with
/// Microsoft Entra authentication, with a token for the Managed Identity (or the Azure CLI login) instead.
/// </summary>
public static class DatabaseConfiguration
{
    public const string EntraAuthenticationKey = "Database:EntraAuthentication";
    public const string ManagedIdentityClientIdKey = "Database:ManagedIdentityClientId";

    // What Azure Database for PostgreSQL accepts tokens for.
    private static readonly TokenRequestContext PostgresTokenRequest = new(["https://ossrdbms-aad.database.windows.net/.default"]);

    /// <param name="credential">Gets the Entra tokens; by default the Managed Identity, or the Azure CLI login locally.</param>
    public static IServiceCollection AddDatabase(this IServiceCollection services, IConfiguration configuration, TokenCredential? credential = null)
    {
        var entraAuthentication = configuration.GetValue<bool>(EntraAuthenticationKey);
        if (entraAuthentication)
        {
            credential ??= AzureCredentials.ManagedIdentityOrAzureCli(
                configuration[ManagedIdentityClientIdKey] is { Length: > 0 } id ? id : null);
        }

        return services.AddDbContextFactory<OverseerDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("Overseer")
                    ?? throw new MissingConfigurationException(
                        "No database is configured. Set the OVERSEER_CONNECTIONSTRINGS__OVERSEER environment variable, " +
                        "or ConnectionStrings:Overseer in the local config file."),
                npgsql =>
                {
                    if (entraAuthentication)
                    {
                        // Tokens last at least an hour; new connections get a fresh one well before it expires.
                        npgsql.ConfigureDataSource(dataSource => dataSource.UsePeriodicPasswordProvider(
                            async (_, cancellationToken) => (await credential!.GetTokenAsync(PostgresTokenRequest, cancellationToken)).Token,
                            successRefreshInterval: TimeSpan.FromMinutes(30),
                            failureRefreshInterval: TimeSpan.FromSeconds(10)));
                    }
                }));
    }
}

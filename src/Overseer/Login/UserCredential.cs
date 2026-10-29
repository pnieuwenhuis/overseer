using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Overseer.Login;

/// <summary>
/// Overseer's single user: a username and the hash of their password, both from configuration.
/// The database never holds the credential (ADR 0005).
/// </summary>
public sealed class UserCredential
{
    public const string NameKey = "User:Name";
    public const string PasswordHashKey = "User:PasswordHash";

    // Identifies the password hash a session was logged in with, without putting the hash in the cookie.
    private const string CredentialClaim = "overseer:credential";

    private readonly string _passwordHash;

    private UserCredential(string name, string passwordHash)
    {
        Name = name;
        _passwordHash = passwordHash;
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash)));
    }

    private string Fingerprint { get; }

    public string Name { get; }

    /// <summary>The logged-in user, as the session cookie carries them.</summary>
    public ClaimsPrincipal CreatePrincipal() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, Name), new Claim(CredentialClaim, Fingerprint)],
        CookieAuthenticationDefaults.AuthenticationScheme));

    /// <summary>Whether a session was logged in with the current credential; one from before a password reset was not.</summary>
    public bool IsCurrent(ClaimsPrincipal? principal) =>
        principal?.FindFirstValue(CredentialClaim) == Fingerprint && principal.Identity?.Name == Name;

    public bool Matches(string username, string password)
    {
        // Always verify, so a wrong username takes as long to reject as a wrong password.
        var passwordMatches = Passwords.Verify(_passwordHash, password);
        return passwordMatches && username == Name;
    }

    /// <exception cref="MissingConfigurationException">No username or no valid password hash is configured.</exception>
    public static UserCredential FromConfiguration(IConfiguration configuration)
    {
        var name = ConfiguredName(configuration);
        var passwordHash = configuration[PasswordHashKey];
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new MissingConfigurationException(
                "No password is configured. Run `overseer reset-password --local` to generate one, " +
                $"or set the {OverseerConfiguration.EnvironmentVariable(PasswordHashKey)} environment variable.");
        }
        if (!Passwords.IsHash(passwordHash))
        {
            throw new MissingConfigurationException(
                $"The configured {PasswordHashKey} is not a password hash. Run `overseer reset-password --local` to generate a new password " +
                "(or `overseer reset-password` for an installation in Azure); it prints the password and configures only its hash.");
        }

        return new UserCredential(name, passwordHash);
    }

    /// <exception cref="MissingConfigurationException">No username is configured.</exception>
    public static string ConfiguredName(IConfiguration configuration)
    {
        var name = configuration[NameKey];
        return string.IsNullOrWhiteSpace(name)
            ? throw new MissingConfigurationException(
                $"No username is configured. Set the {OverseerConfiguration.EnvironmentVariable(NameKey)} environment variable, " +
                $"or {NameKey} in the local config file.")
            : name;
    }
}

namespace Overseer.Login;

/// <summary>
/// Logging in, guarded against guessing: after <see cref="MaxFailedLogins"/> consecutive failed logins, every login is
/// refused for <see cref="LockoutPeriod"/>, even one with the correct password. The count lives in memory only, so a
/// restart resets it; Overseer runs as a single replica.
/// </summary>
public sealed class AccountLockout(UserCredential credential, TimeProvider time)
{
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockoutPeriod = TimeSpan.FromMinutes(15);

    // One login at a time, so concurrent guesses can't slip past the count.
    private readonly Lock _lock = new();
    private int _failedLogins;
    private DateTimeOffset _lockedUntil = DateTimeOffset.MinValue;

    public LoginResult TryLogIn(string username, string password)
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            if (now < _lockedUntil)
            {
                return new LoginResult.LockedOut(_lockedUntil);
            }
            if (credential.Matches(username, password))
            {
                _failedLogins = 0;
                return new LoginResult.LoggedIn();
            }
            if (++_failedLogins < MaxFailedLogins)
            {
                return new LoginResult.Failed();
            }
            _failedLogins = 0;
            _lockedUntil = now + LockoutPeriod;
            return new LoginResult.LockedOut(_lockedUntil);
        }
    }
}

public abstract record LoginResult
{
    private LoginResult()
    {
    }

    public sealed record LoggedIn : LoginResult;

    /// <summary>The username or password is wrong.</summary>
    public sealed record Failed : LoginResult;

    /// <summary>Refused without checking the username and password, until the lockout ends.</summary>
    public sealed record LockedOut(DateTimeOffset Until) : LoginResult;
}

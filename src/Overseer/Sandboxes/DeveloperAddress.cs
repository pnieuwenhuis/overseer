using System.Net;
using System.Net.Sockets;

namespace Overseer.Sandboxes;

/// <summary>The public IP address this machine reaches the internet from: the only one a local-mode Sandbox lets in.</summary>
public static class DeveloperAddress
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <exception cref="SandboxCreationFailedException">The address could not be found out.</exception>
    public static async Task<IPAddress> CurrentAsync(CancellationToken cancellationToken)
    {
        string answer;
        try
        {
            // ipify answers over IPv4 only, which is also how Overseer reaches a Sandbox's public IP.
            answer = (await Http.GetStringAsync("https://api.ipify.org", cancellationToken)).Trim();
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new SandboxCreationFailedException(
                $"Overseer could not find out this machine's public IP address, the only one a Sandbox lets in, from api.ipify.org: {exception.Message}");
        }
        return IPAddress.TryParse(answer, out var address) && address.AddressFamily == AddressFamily.InterNetwork
            ? address
            : throw new SandboxCreationFailedException($"api.ipify.org gave '{answer}' as this machine's public IP address, which is not an IPv4 address.");
    }
}

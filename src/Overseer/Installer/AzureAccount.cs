namespace Overseer.Installer;

/// <summary>The subscription the Azure CLI uses, and who it is logged in as: where installer commands look and make changes.</summary>
internal sealed record AzureAccount(string SubscriptionId, string SubscriptionName, string User)
{
    /// <exception cref="AzureScriptException">The Azure CLI is missing or not logged in; the message says what to do.</exception>
    public static async Task<AzureAccount> CurrentAsync(AzureScripts azure, CancellationToken cancellationToken)
    {
        var account = AzureSteps.Lines(await azure.RunAsync("check-azure-cli", new Dictionary<string, string>(), cancellationToken));
        return new AzureAccount(account[0], account[1], account[2]);
    }

    /// <summary>Says which subscription a command uses, and how to pick another.</summary>
    public string Using => $"Using the subscription {SubscriptionName} ({SubscriptionId}) as {User}. `az account set` selects another subscription.";
}

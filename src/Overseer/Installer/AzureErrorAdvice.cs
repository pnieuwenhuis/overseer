namespace Overseer.Installer;

/// <summary>What to do about the Azure errors install and uninstall are most likely to run into.</summary>
internal static class AzureErrorAdvice
{
    public static string For(string azureError, string region)
    {
        if (Mentions(azureError, "AuthorizationFailed", "does not have authorization"))
        {
            return "Your Azure account lacks a permission install needs. Install creates a role assignment, so it needs Owner on the " +
                "subscription, or Contributor plus User Access Administrator. Ask an owner of the subscription to grant that, or to run install.";
        }
        if (Mentions(azureError, "RequestDisallowedByPolicy"))
        {
            return "An Azure Policy on the subscription forbids this. Ask the subscription's administrator for an exemption, " +
                "or install into another subscription.";
        }
        if (Mentions(azureError, "quota"))
        {
            return $"The subscription has reached a quota in {region}. Request an increase under Quotas in the Azure portal, " +
                "or install in another region with --region.";
        }
        if (Mentions(azureError, "LocationNotAvailable", "LocationIsOfferRestricted", "NoRegisteredProviderFound", "SkuNotAvailable",
                "not available in", "restricted from provisioning"))
        {
            return $"Something Overseer needs is not available to this subscription in {region}. " +
                "Choose another region with --region, such as northeurope or swedencentral.";
        }
        return "Fix the cause and run install again.";
    }

    public static string ForUninstall(string azureError)
    {
        if (Mentions(azureError, "AuthorizationFailed", "does not have authorization"))
        {
            return "Your Azure account lacks a permission uninstall needs. It deletes a role assignment, so it needs Owner on the " +
                "subscription, or Contributor plus User Access Administrator, as install does. Ask an owner of the subscription to grant that, or to run uninstall.";
        }
        if (Mentions(azureError, "RequestDisallowedByPolicy"))
        {
            return "An Azure Policy on the subscription forbids this. Ask the subscription's administrator for an exemption.";
        }
        return "Fix the cause.";
    }

    private static bool Mentions(string azureError, params string[] phrases) =>
        phrases.Any(phrase => azureError.Contains(phrase, StringComparison.OrdinalIgnoreCase));
}

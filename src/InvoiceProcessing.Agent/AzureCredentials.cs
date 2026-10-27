using Azure.Core;
using Azure.Identity;

namespace InvoiceProcessing;

public static class AzureCredentials
{
    /// <summary>
    /// In the hosted agent container (the platform sets IDENTITY_ENDPOINT) use the agent's own identity
    /// only - no credential probing chain. Locally fall back to the developer's sign-in (az / azd /
    /// Visual Studio) via <see cref="DefaultAzureCredential"/>.
    /// A twin of the function's copy: each project is deployed from its own folder, so they can't share a file.
    /// </summary>
    public static TokenCredential Create() =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT"))
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
}

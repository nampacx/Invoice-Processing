using Azure.Core;
using Azure.Identity;

namespace InvoiceProcessing;

public static class AzureCredentials
{
    /// <summary>
    /// In Azure (Functions sets IDENTITY_ENDPOINT when a managed identity is enabled) use the app's
    /// system-assigned managed identity only - no credential probing chain. Locally fall back to the
    /// developer's sign-in (az / azd / Visual Studio) via <see cref="DefaultAzureCredential"/>.
    /// </summary>
    public static TokenCredential Create() =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT"))
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
}

using Azure.Core;
using Azure.Identity;
using NewsService.Configuration;
using System.Security.Cryptography.X509Certificates;

namespace NewsService.Services;

/// <summary>
/// Builds the Azure AD <see cref="TokenCredential"/> from <see cref="AzureBlobSection"/>.
/// Extracted from <see cref="AzureBlobRepositoryReader"/> so the same credential — and the
/// same single app registration — can be reused for Microsoft Graph (Entra device read)
/// independently of <c>Repository:StorageMode</c>. The Entra feature requires these values to
/// be populated even when StorageMode=Share.
/// </summary>
public static class AzureCredentialFactory
{
    public static TokenCredential Create(AzureBlobSection cfg)
    {
        if (cfg.AuthMode.Equals("ClientSecret", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(cfg.TenantId) ||
                string.IsNullOrWhiteSpace(cfg.ClientId) ||
                string.IsNullOrWhiteSpace(cfg.ClientSecret))
                throw new InvalidOperationException(
                    "AzureBlob:AuthMode=ClientSecret requires TenantId, ClientId, and ClientSecret.");

            return new ClientSecretCredential(cfg.TenantId, cfg.ClientId, cfg.ClientSecret);
        }

        // Default: Certificate
        if (string.IsNullOrWhiteSpace(cfg.TenantId) ||
            string.IsNullOrWhiteSpace(cfg.ClientId) ||
            string.IsNullOrWhiteSpace(cfg.CertificateThumbprint))
            throw new InvalidOperationException(
                "AzureBlob:AuthMode=Certificate requires TenantId, ClientId, and CertificateThumbprint.");

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var certs = store.Certificates.Find(
            X509FindType.FindByThumbprint, cfg.CertificateThumbprint, validOnly: false);

        if (certs.Count == 0)
            throw new InvalidOperationException(
                $"Certificate with thumbprint '{cfg.CertificateThumbprint}' not found in LocalMachine\\My.");

        return new ClientCertificateCredential(cfg.TenantId, cfg.ClientId, certs[0]);
    }
}

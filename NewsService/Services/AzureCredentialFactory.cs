using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using NewsCentral.Configuration;
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
    /// <param name="transport">
    /// Optional Azure.Core transport (the shared WinHTTP transport from
    /// <see cref="AzureProxyTransportFactory"/>). When non-null it is applied to the credential's
    /// AAD token calls so they ride the machine WinHTTP proxy too. When null, the credential is
    /// constructed exactly as before — no options object, today's behavior byte-for-byte.
    /// </param>
    /// <param name="readEnv">
    /// Test seam for the ClientSecretEnv mode. When null (production), the secret is read
    /// machine-scope (EnvironmentVariableTarget.Machine) — live from the registry, not from the
    /// environment inherited at process start.
    /// </param>
    public static TokenCredential Create(AzureBlobSection cfg, HttpClientTransport? transport = null,
        Func<string, string?>? readEnv = null)
    {
        if (cfg.AuthMode.Equals("ClientSecretEnv", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(cfg.TenantId) ||
                string.IsNullOrWhiteSpace(cfg.ClientId))
                throw new InvalidOperationException(
                    "AzureBlob:AuthMode=ClientSecretEnv requires TenantId and ClientId.");

            readEnv ??= static name =>
                Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
            var secret = readEnv(SolutionConstants.NewsServiceAzureClientSecretEnvVar);
            if (string.IsNullOrWhiteSpace(secret))
                throw new InvalidOperationException(
                    $"AzureBlob:AuthMode=ClientSecretEnv: machine environment variable " +
                    $"{SolutionConstants.NewsServiceAzureClientSecretEnvVar} is not set or empty — " +
                    "refusing to authenticate (no fallback to the registry ClientSecret).");

            if (transport is null)
                return new ClientSecretCredential(cfg.TenantId, cfg.ClientId, secret);

            return new ClientSecretCredential(cfg.TenantId, cfg.ClientId, secret,
                new ClientSecretCredentialOptions { Transport = transport });
        }

        if (cfg.AuthMode.Equals("ClientSecret", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(cfg.TenantId) ||
                string.IsNullOrWhiteSpace(cfg.ClientId) ||
                string.IsNullOrWhiteSpace(cfg.ClientSecret))
                throw new InvalidOperationException(
                    "AzureBlob:AuthMode=ClientSecret requires TenantId, ClientId, and ClientSecret.");

            if (transport is null)
                return new ClientSecretCredential(cfg.TenantId, cfg.ClientId, cfg.ClientSecret);

            return new ClientSecretCredential(cfg.TenantId, cfg.ClientId, cfg.ClientSecret,
                new ClientSecretCredentialOptions { Transport = transport });
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

        if (transport is null)
            return new ClientCertificateCredential(cfg.TenantId, cfg.ClientId, certs[0]);

        return new ClientCertificateCredential(cfg.TenantId, cfg.ClientId, certs[0],
            new ClientCertificateCredentialOptions { Transport = transport });
    }
}

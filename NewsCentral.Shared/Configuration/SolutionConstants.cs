namespace NewsCentral.Configuration;

/// <summary>
/// Fixed, non-configurable identifiers shared across the solution.
/// </summary>
public static partial class SolutionConstants
{
    /// <summary>
    /// The solution segment of the registry hive path
    /// (HKLM\Software\{Company}\{SolutionName}\{Component}). Fixed constant — the
    /// per-component registry subtree is anchored on this, not on any config value.
    /// </summary>
    public const string SolutionName = "NewsCentral";

    /// <summary>
    /// Machine-scope environment variable holding the Azure client secret for NewsService's
    /// AzureBlob:AuthMode=ClientSecretEnv. NewsService-only — NewsCentral is an interactive
    /// per-user app with no LocalSystem context and does not support this mode.
    /// </summary>
    public const string NewsServiceAzureClientSecretEnvVar = "NEWSSERVICE_AZURE_CLIENTSECRET";

    // Company (the {Company} hive segment) is generated into this partial class from the
    // <Company> MSBuild property — see NewsCentral.Shared.csproj / Directory.Build.props.
}

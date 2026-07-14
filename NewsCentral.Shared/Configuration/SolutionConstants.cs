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

    // Company (the {Company} hive segment) is generated into this partial class from the
    // <Company> MSBuild property — see NewsCentral.Shared.csproj / Directory.Build.props.
}

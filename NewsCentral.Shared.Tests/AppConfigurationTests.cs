namespace NewsCentral.Shared.Tests;

public sealed class AppConfigurationTests
{
    // M5b: the admin/admin first-run seed and its two Initialization:* keys were removed —
    // AppConfiguration.DefaultAdminUsername/DefaultAdminPassword no longer exist. DataPath is
    // exercised elsewhere (EnvironmentSettingsTests.FromMachineConfiguration_MapsEveryField); this
    // class is kept as the home for any future AppConfiguration-specific test, empty for now.
}

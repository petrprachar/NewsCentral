namespace NewsCentral.Localization;

/// <summary>
/// L10N-1: one supported UI language — <see cref="CultureName"/> is both the resx culture suffix
/// (e.g. <c>Settings.de.resx</c>) and the value stored in <c>User.PreferredUiLanguage</c>.
/// </summary>
public sealed record UiLanguage(string CultureName, string NativeName);

/// <summary>The four supported UI languages, in display order.</summary>
public static class UiLanguages
{
    public static readonly UiLanguage English = new("en", "English");
    public static readonly UiLanguage German = new("de", "Deutsch");
    public static readonly UiLanguage Spanish = new("es", "Español");
    public static readonly UiLanguage French = new("fr", "Français");

    public static readonly IReadOnlyList<UiLanguage> All = new[] { English, German, Spanish, French };
}

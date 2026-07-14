namespace NewsCentral.Models
{
    /// <summary>
    /// Single source of truth for presentation display-duration semantics.
    /// <para>
    /// <see cref="DisplayDurationSeconds"/> is the default poster lifetime in seconds — referenced by
    /// both the model default (<see cref="Presentation.DisplayDurationSeconds"/>) and the NewsViewer
    /// fallback, so the value 30 is never hardcoded anywhere else.
    /// </para>
    /// <para>
    /// A raw duration is resolved via <see cref="ResolveDuration"/>: <c>-1</c> is the reserved
    /// never-auto-close sentinel (unreachable until the authoring UI ships), <c>0</c> means "unset"
    /// (the state every existing presentation carries) and resolves to the default, and any other
    /// negative value is treated as unset too — never as never-close.
    /// </para>
    /// </summary>
    public static class PresentationDefaults
    {
        /// <summary>Default poster lifetime in seconds when a presentation carries no explicit value.</summary>
        public const int DisplayDurationSeconds = 30;

        /// <summary>Reserved sentinel: never auto-close. Unreachable until the authoring UI ships.</summary>
        public const int NeverAutoClose = -1;

        /// <summary>
        /// Resolves a raw <c>DisplayDurationSeconds</c> to an effective value.
        /// <para><c>-1</c> → <c>-1</c> (never auto-close); <c>&lt;= 0</c> → <see cref="DisplayDurationSeconds"/>
        /// (unset); <c>&gt; 0</c> → the value as-is.</para>
        /// The <see cref="NeverAutoClose"/> check MUST precede the unset check so it is not swallowed by it.
        /// </summary>
        public static int ResolveDuration(int raw)
        {
            if (raw == NeverAutoClose) return NeverAutoClose;
            if (raw <= 0) return DisplayDurationSeconds;
            return raw;
        }
    }
}

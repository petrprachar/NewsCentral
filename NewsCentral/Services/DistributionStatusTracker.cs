namespace NewsCentral.Services;

/// <summary>
/// Records the most recent outcome of every distribution call that went through a REAL inner
/// service (<see cref="DistributionServiceRouter"/> is the only writer) — never a call handled by
/// <see cref="NullBlobDistributionService"/>, since distribution being deliberately disabled is not
/// a failure. Singleton: one instance's state describes the current environment's distribution
/// health machine-wide, for MainLayout's admin banner and the Environment Management page.
/// </summary>
public sealed class DistributionStatusTracker
{
    public DateTime? LastSuccessUtc { get; private set; }
    public DateTime? LastFailureUtc { get; private set; }
    public string? LastFailureReason { get; private set; }
    public string? LastFailurePath { get; private set; }

    /// <summary>
    /// True when the most recent attempt failed and no later success has superseded it. A fresh
    /// tracker (nothing attempted yet) is not failing.
    /// </summary>
    public bool IsFailing =>
        LastFailureUtc.HasValue && (!LastSuccessUtc.HasValue || LastFailureUtc.Value > LastSuccessUtc.Value);

    public event Action? Changed;

    public DistributionStatusTracker(EnvironmentContext environment)
    {
        // A different environment has a different distribution target entirely — carrying over a
        // failure (or success) recorded against the PREVIOUS environment would be misleading.
        environment.Changed += Reset;
    }

    public void RecordSuccess()
    {
        LastSuccessUtc = DateTime.UtcNow;
        Changed?.Invoke();
    }

    public void RecordFailure(string path, string reason)
    {
        LastFailureUtc = DateTime.UtcNow;
        LastFailureReason = reason;
        LastFailurePath = path;
        Changed?.Invoke();
    }

    public void Reset()
    {
        LastSuccessUtc = null;
        LastFailureUtc = null;
        LastFailureReason = null;
        LastFailurePath = null;
        Changed?.Invoke();
    }
}

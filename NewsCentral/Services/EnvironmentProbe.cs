namespace NewsCentral.Services;

/// <summary>
/// Result of <see cref="EnvironmentProbe.ProbeAsync"/>. <see cref="Exists"/> and
/// <see cref="Initialized"/> are only meaningful when <see cref="TimedOut"/> is false and
/// <see cref="Error"/> is null.
/// </summary>
public sealed record ProbeResult(bool Exists, bool Initialized, bool TimedOut, string? Error);

/// <summary>
/// The ONLY place in NewsCentral that touches the filesystem to check whether a candidate
/// environment path is reachable — used solely by <see cref="EnvironmentDirectoryService.AddAsync"/>
/// when a user adds a new environment. The picker itself never probes a listed entry; see
/// <see cref="EnvironmentListBuilder"/>, which renders from stored data only.
/// </summary>
public static class EnvironmentProbe
{
    /// <summary>
    /// Checks <paramref name="path"/> on a background thread, bounded by <paramref name="timeout"/>
    /// — important because an unreachable UNC path can block for a long time on a dead/slow server.
    /// </summary>
    public static async Task<ProbeResult> ProbeAsync(string path, TimeSpan timeout)
    {
        var probeTask = Task.Run(() =>
        {
            try
            {
                var exists = Directory.Exists(path);
                var initialized = exists && File.Exists(Path.Combine(path, "config", "users.json"));
                return new ProbeResult(exists, initialized, TimedOut: false, Error: null);
            }
            catch (Exception ex)
            {
                return new ProbeResult(false, false, TimedOut: false, Error: ex.Message);
            }
        });

        var winner = await Task.WhenAny(probeTask, Task.Delay(timeout));
        if (winner != probeTask)
            return new ProbeResult(false, false, TimedOut: true, Error: null);

        return await probeTask;
    }
}

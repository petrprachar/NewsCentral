using System.Collections.Concurrent;

namespace NewsCentral.Configuration;

/// <summary>
/// Async memo cache that keeps only results worth keeping. Concurrent callers for a key share one
/// in-flight Task; when it completes, a faulted/cancelled Task or a result for which the keep
/// predicate is false is evicted (only if the stored Task is still that one), so the next call
/// re-runs the factory. Keys compare OrdinalIgnoreCase.
/// </summary>
public sealed class KeepWhenCache<T>
{
    private readonly Func<T, bool> _keep;
    private readonly ConcurrentDictionary<string, Task<T>> _entries = new(StringComparer.OrdinalIgnoreCase);

    public KeepWhenCache(Func<T, bool> keep)
    {
        _keep = keep ?? throw new ArgumentNullException(nameof(keep));
    }

    public Task<T> GetOrAdd(string key, Func<Task<T>> factory)
    {
        while (true)
        {
            if (_entries.TryGetValue(key, out var existing))
                return existing;

            // Placeholder lets us register before the factory runs, so the continuation can
            // compare by reference even when the factory completes synchronously.
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_entries.TryAdd(key, tcs.Task))
                continue;

            RunFactory(key, factory, tcs);
            return tcs.Task;
        }
    }

    public void Remove(string key) => _entries.TryRemove(key, out _);

    private async void RunFactory(string key, Func<Task<T>> factory, TaskCompletionSource<T> tcs)
    {
        try
        {
            var result = await factory().ConfigureAwait(false);
            if (!_keep(result))
                _entries.TryRemove(new KeyValuePair<string, Task<T>>(key, tcs.Task));
            tcs.TrySetResult(result);
        }
        catch (Exception ex)
        {
            _entries.TryRemove(new KeyValuePair<string, Task<T>>(key, tcs.Task));
            tcs.TrySetException(ex);
        }
    }
}

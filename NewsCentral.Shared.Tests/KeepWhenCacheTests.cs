using NewsCentral.Configuration;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class KeepWhenCacheTests
{
    private static KeepWhenCache<int> Cache() => new(v => v > 0);

    [Fact]
    public async Task KeptResult_FactoryCalledOnce()
    {
        var cache = Cache(); var calls = 0;
        Task<int> F() { calls++; return Task.FromResult(1); }
        await cache.GetOrAdd("k", F);
        await cache.GetOrAdd("k", F);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NotKeptResult_FactoryCalledAgain()
    {
        var cache = Cache(); var calls = 0;
        Task<int> F() { calls++; return Task.FromResult(calls == 1 ? 0 : 1); }
        Assert.Equal(0, await cache.GetOrAdd("k", F));
        Assert.Equal(1, await cache.GetOrAdd("k", F));
        Assert.Equal(1, await cache.GetOrAdd("k", F));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FaultedFactory_PropagatesThenRetries()
    {
        var cache = Cache(); var calls = 0;
        Task<int> F() { calls++; if (calls == 1) throw new IOException("x"); return Task.FromResult(1); }
        await Assert.ThrowsAsync<IOException>(() => cache.GetOrAdd("k", F));
        Assert.Equal(1, await cache.GetOrAdd("k", F));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ConcurrentCalls_ShareOneInvocation()
    {
        var cache = Cache(); var calls = 0;
        var gate = new TaskCompletionSource<int>();
        Task<int> F() { Interlocked.Increment(ref calls); return gate.Task; }
        var t1 = cache.GetOrAdd("k", F);
        var t2 = cache.GetOrAdd("k", F);
        gate.SetResult(5);
        Assert.Equal(5, await t1);
        Assert.Equal(5, await t2);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Remove_ForcesRerun()
    {
        var cache = Cache(); var calls = 0;
        Task<int> F() { calls++; return Task.FromResult(1); }
        await cache.GetOrAdd("k", F);
        cache.Remove("k");
        await cache.GetOrAdd("k", F);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task KeysDifferingByCase_ShareEntry()
    {
        var cache = Cache(); var calls = 0;
        Task<int> F() { calls++; return Task.FromResult(1); }
        await cache.GetOrAdd("C:/Data", F);
        await cache.GetOrAdd("c:/data", F);
        Assert.Equal(1, calls);
    }
}

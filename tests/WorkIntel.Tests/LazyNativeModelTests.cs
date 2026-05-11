using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkIntel.Pipeline;
using Xunit;

namespace WorkIntel.Tests;

/// <summary>
/// Lifecycle contract for <see cref="LazyNativeModel"/>. We don't test the real
/// Whisper / LLama subclasses here (those need GB-scale model files); a tiny
/// test-double subclass exposes the protected surface and lets us inject delays
/// and failures.
/// </summary>
public sealed class LazyNativeModelTests
{
    /// <summary>Test double — instrumented load/release so tests can assert call counts
    /// and inject artificial latency or failures.</summary>
    private sealed class FakeModel : LazyNativeModel
    {
        public int LoadCount;
        public int ReleaseCount;
        public TimeSpan LoadDelay = TimeSpan.Zero;
        public Exception? LoadFailWith;

        protected override async Task LoadCoreAsync(CancellationToken ct)
        {
            if (LoadDelay > TimeSpan.Zero) await Task.Delay(LoadDelay, ct).ConfigureAwait(false);
            if (LoadFailWith is not null) throw LoadFailWith;
            Interlocked.Increment(ref LoadCount);
        }

        protected override ValueTask ReleaseResourcesAsync()
        {
            Interlocked.Increment(ref ReleaseCount);
            return ValueTask.CompletedTask;
        }

        // Re-expose protected surface for tests.
        public new Task<bool> EnsureInitializedAsync(CancellationToken ct = default) =>
            base.EnsureInitializedAsync(ct);

        public new Task<T> RunSerializedAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken ct = default) =>
            base.RunSerializedAsync(body, ct);
    }

    [Fact]
    public async Task Warmup_IsIdempotent()
    {
        await using var m = new FakeModel();

        await m.WarmupAsync();
        await m.WarmupAsync();
        await m.WarmupAsync();

        Assert.Equal(1, m.LoadCount);
        Assert.True(m.IsInitialized);
        Assert.False(m.InitFailed);
    }

    [Fact]
    public async Task ConcurrentWarmups_LoadOnceUnderRace()
    {
        await using var m = new FakeModel { LoadDelay = TimeSpan.FromMilliseconds(50) };

        var tasks = Enumerable.Range(0, 16).Select(_ => m.WarmupAsync()).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, m.LoadCount);
        Assert.True(m.IsInitialized);
    }

    [Fact]
    public async Task LoadFailure_IsCachedAndDoesNotRetry()
    {
        await using var m = new FakeModel { LoadFailWith = new InvalidOperationException("boom") };

        var first = await m.EnsureInitializedAsync();
        var second = await m.EnsureInitializedAsync();
        var third = await m.EnsureInitializedAsync();

        Assert.False(first);
        Assert.False(second);
        Assert.False(third);
        Assert.True(m.InitFailed);
        Assert.False(m.IsInitialized);
        // LoadCount tracks *successful* loads only (FakeModel increments after the throw check).
        Assert.Equal(0, m.LoadCount);
    }

    [Fact]
    public async Task EnsureInitialized_ReturnsTrueOnSuccess()
    {
        await using var m = new FakeModel();
        Assert.True(await m.EnsureInitializedAsync());
    }

    [Fact]
    public async Task RunSerialized_ExecutesBody_AndReturnsResult()
    {
        await using var m = new FakeModel();
        await m.WarmupAsync();

        var result = await m.RunSerializedAsync(_ => Task.FromResult(42));

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task RunSerialized_SerializesConcurrentBodies()
    {
        await using var m = new FakeModel();
        await m.WarmupAsync();

        int concurrent = 0;
        int peakConcurrent = 0;
        var lockObj = new object();

        async Task<int> Body(CancellationToken ct)
        {
            int now;
            lock (lockObj)
            {
                concurrent++;
                if (concurrent > peakConcurrent) peakConcurrent = concurrent;
                now = concurrent;
            }
            await Task.Delay(20, ct).ConfigureAwait(false);
            lock (lockObj) concurrent--;
            return now;
        }

        var tasks = Enumerable.Range(0, 5).Select(_ => m.RunSerializedAsync(Body)).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, peakConcurrent);
    }

    [Fact]
    public async Task Dispose_DrainsInFlightInference_BeforeReleasingResources()
    {
        var m = new FakeModel();
        await m.WarmupAsync();

        var inflightStarted = new TaskCompletionSource();
        var releaseInflight = new TaskCompletionSource();

        // Park a slow operation under the process lock.
        var inflight = Task.Run(async () =>
        {
            await m.RunSerializedAsync<bool>(async _ =>
            {
                inflightStarted.SetResult();
                await releaseInflight.Task.ConfigureAwait(false);
                return true;
            });
        });

        await inflightStarted.Task; // confirm we hold the process lock

        // Dispose should block until the inflight op finishes.
        var disposeTask = m.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(disposeTask.IsCompleted);
        Assert.Equal(0, m.ReleaseCount);

        releaseInflight.SetResult();
        await inflight;
        await disposeTask;

        Assert.Equal(1, m.ReleaseCount);
    }

    [Fact]
    public async Task Dispose_TimesOut_OnHungInference_AndReleasesAnyway()
    {
        var m = new FakeModel { DisposeDrainTimeout = TimeSpan.FromMilliseconds(80) };
        await m.WarmupAsync();

        // Acquire the process lock and never release it — simulates a hung native call.
        var hung = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            await m.RunSerializedAsync<bool>(async _ =>
            {
                await hung.Task.ConfigureAwait(false);
                return true;
            });
        });

        // Give the background task time to grab the lock.
        await Task.Delay(20);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await m.DisposeAsync();
        sw.Stop();

        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(50),
            $"expected dispose to wait at least the drain timeout; took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(1, m.ReleaseCount);

        // Unblock the hung task so it doesn't sit forever.
        hung.SetResult();
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var m = new FakeModel();
        await m.WarmupAsync();

        await m.DisposeAsync();
        await m.DisposeAsync();
        await m.DisposeAsync();

        Assert.Equal(1, m.ReleaseCount);
    }

    [Fact]
    public async Task EnsureInitialized_AfterDispose_Throws()
    {
        var m = new FakeModel();
        await m.WarmupAsync();
        await m.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await m.EnsureInitializedAsync());
    }

    [Fact]
    public async Task Dispose_BeforeAnyInit_StillReleasesResources()
    {
        // ReleaseResourcesAsync should still be called even if Load never ran —
        // subclasses might allocate fields lazily and we don't want a leak path.
        var m = new FakeModel();

        await m.DisposeAsync();

        Assert.Equal(1, m.ReleaseCount);
        Assert.False(m.IsInitialized);
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using WorkIntel.App;

namespace WorkIntel.Pipeline;

/// <summary>
/// Shared scaffolding for "lazily-loaded native model" components — Whisper,
/// LLamaSharp, future audio backends. Captures the three lifecycle invariants
/// these components share and which are easy to subtly get wrong individually:
/// <list type="number">
///   <item>One-time initialization, even under concurrent first-touch (init lock).</item>
///   <item>Serialized inference — most native runtimes are not thread-safe (process lock).</item>
///   <item>Dispose drains in-flight inference before releasing native handles, with a timeout.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Subclasses implement <see cref="LoadCoreAsync"/> (populate native fields) and
/// <see cref="ReleaseResourcesAsync"/> (dispose them). For each operation,
/// subclasses call <see cref="EnsureInitializedAsync"/> first and bail with a
/// soft-fail value if it returns <c>false</c> — then run the actual native
/// inference inside <see cref="RunSerializedAsync"/>.
/// </para>
/// <para>
/// The base deliberately doesn't try to be generic over "the loaded thing".
/// Whisper has a factory + processor pair; LLamaSharp has weights + model params.
/// Letting subclasses keep their own typed fields avoids contortions and keeps
/// the abstraction small.
/// </para>
/// </remarks>
public abstract class LazyNativeModel : IAsyncDisposable
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _processLock = new(1, 1);
    private bool _initialized;
    private bool _initFailed;
    private bool _disposed;

    /// <summary>How long <see cref="DisposeAsync"/> waits for in-flight inference
    /// to complete before forcing native handle release.</summary>
    public TimeSpan DisposeDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>True if <see cref="LoadCoreAsync"/> has run successfully.</summary>
    public bool IsInitialized => Volatile.Read(ref _initialized);

    /// <summary>True if <see cref="LoadCoreAsync"/> threw and we've stopped retrying for this session.</summary>
    public bool InitFailed => Volatile.Read(ref _initFailed);

    /// <summary>Eager-load the native model. Safe to call multiple times concurrently;
    /// only the first call performs the load.</summary>
    public Task WarmupAsync(CancellationToken ct = default) => EnsureInitializedAsync(ct);

    /// <summary>
    /// Ensures <see cref="LoadCoreAsync"/> has run. Returns <c>true</c> on success,
    /// <c>false</c> if init has previously failed (we don't retry within a session —
    /// repeated failed loads of a multi-GB model on the hot path would be hostile).
    /// </summary>
    protected async Task<bool> EnsureInitializedAsync(CancellationToken ct)
    {
        // Disposed-check must come FIRST. After a successful warmup, _initialized
        // stays true even after Dispose, so checking _initialized before _disposed
        // would let post-dispose callers slip through and then crash on disposed
        // native handles inside RunSerializedAsync.
        if (Volatile.Read(ref _disposed)) throw new ObjectDisposedException(GetType().Name);
        if (Volatile.Read(ref _initialized)) return true;
        if (Volatile.Read(ref _initFailed)) return false;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check inside the lock: a concurrent Dispose could have set _disposed
            // between our outer check and our lock acquisition.
            if (Volatile.Read(ref _disposed)) throw new ObjectDisposedException(GetType().Name);
            if (_initialized) return true;
            if (_initFailed) return false;

            try
            {
                await LoadCoreAsync(ct).ConfigureAwait(false);
                Volatile.Write(ref _initialized, true);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _initFailed, true);
                Log.Error($"{GetType().Name} initialization failed; this session will skip operations", ex);
                return false;
            }
        }
        finally
        {
            try { _initLock.Release(); } catch (ObjectDisposedException) { /* race with dispose, fine */ }
        }
    }

    /// <summary>
    /// Subclass populates its native fields here. Throw to mark init as failed —
    /// the exception is logged and subsequent calls will short-circuit to <c>false</c>.
    /// </summary>
    protected abstract Task LoadCoreAsync(CancellationToken ct);

    /// <summary>Subclass disposes its native fields here. Called even if init failed
    /// or never ran, so check for nulls.</summary>
    protected abstract ValueTask ReleaseResourcesAsync();

    /// <summary>
    /// Run an inference body under the process lock. Most native runtimes are not safe for
    /// concurrent inference, so we serialize. Caller is expected to have already called
    /// <see cref="EnsureInitializedAsync"/>.
    /// </summary>
    protected async Task<T> RunSerializedAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken ct)
    {
        await _processLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await body(ct).ConfigureAwait(false);
        }
        finally
        {
            try { _processLock.Release(); } catch (ObjectDisposedException) { /* race with dispose */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed)) return;
        Volatile.Write(ref _disposed, true);

        // Drain any in-flight inference before tearing down native handles. We acquire the
        // process lock with a bounded wait — a deadlocked native call shouldn't block shutdown
        // forever, even at the cost of a possible crash on the in-flight inference.
        bool drained = false;
        try
        {
            drained = await _processLock.WaitAsync(DisposeDrainTimeout).ConfigureAwait(false);
            if (!drained)
                Log.Warn($"{GetType().Name} dispose timed out waiting for in-flight inference; releasing anyway");
        }
        catch (Exception ex)
        {
            Log.Warn($"{GetType().Name} dispose drain wait threw: {ex.Message}");
        }

        try
        {
            await ReleaseResourcesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"{GetType().Name} resource release failed: {ex.Message}");
        }
        finally
        {
            if (drained)
            {
                try { _processLock.Release(); } catch { /* about to dispose anyway */ }
            }
        }

        _initLock.Dispose();
        _processLock.Dispose();
    }
}

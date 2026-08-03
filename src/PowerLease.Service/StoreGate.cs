using PowerLease.Application.Hosting;
using PowerLease.Application.Kernel;
using PowerLease.Persistence.History;

namespace PowerLease.Service;

internal sealed class StoreGate
{
    public StoreGate(SqliteHistoryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        SyncRoot = store;
    }

    public object SyncRoot { get; }
}

internal sealed class SynchronizedEffectExecutor : IEffectExecutor
{
    private readonly IEffectExecutor _inner;
    private readonly object _syncRoot;

    public SynchronizedEffectExecutor(IEffectExecutor inner, object syncRoot)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(syncRoot);
        _inner = inner;
        _syncRoot = syncRoot;
    }

    /// <summary>
    /// Run the effect while holding the store lock.
    /// <para>
    /// This works only because the inner executor finishes its work before it returns its task --
    /// <see cref="PowerLease.Persistence.History.SqliteEffectExecutor" /> writes synchronously and hands back
    /// an already-completed task. A lock cannot be held across an await, so an executor that really did go
    /// asynchronous would release this the instant it returned, leaving the store unguarded while the write
    /// was still running, and nothing here would say so. If that ever changes, this must become a
    /// <see cref="SemaphoreSlim" /> and every other user of the same lock must move with it.
    /// </para>
    /// </summary>
    public Task<EffectCompletion> ExecuteAsync(KernelEffect effect, CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            return _inner.ExecuteAsync(effect, cancellationToken);
        }
    }
}

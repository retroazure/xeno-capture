using System;
using System.Threading;
using System.Threading.Tasks;

namespace XenoCapture.Engine;

// Publish completion before invoking user code, so concurrent callers share
// one cleanup operation even when it completes synchronously or faults.
internal sealed class SharedOperation(bool repeatable = false)
{
    readonly object gate = new();
    Task? current;
    public Task? Current { get { lock (gate) return current; } }
    public Task Run(Func<Task> action)
    {
        TaskCompletionSource completion;
        lock (gate)
        {
            if (current != null) return current;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            current = completion.Task;
        }
        _ = Execute(action, completion);
        return completion.Task;
    }
    async Task Execute(Func<Task> action, TaskCompletionSource completion)
    {
        Exception? error = null;
        try { await action().ConfigureAwait(false); } catch (Exception ex) { error = ex; }
        if (repeatable) { lock (gate) { if (ReferenceEquals(current, completion.Task)) current = null; } }
        if (error == null) completion.TrySetResult(); else completion.TrySetException(error);
    }
}
internal sealed class ActionScope(Action onDispose) : IDisposable
{
    Action? action = onDispose;
    public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
}
internal sealed class OperationCounter
{
    int count;
    public bool IsBusy => Volatile.Read(ref count) != 0;
    public IDisposable Enter()
    {
        Interlocked.Increment(ref count);
        return new ActionScope(() => Interlocked.Decrement(ref count));
    }
}
internal sealed class SessionLifetime
{
    readonly object gate = new();
    int generation, active, closed;
    public int Generation => Volatile.Read(ref generation);
    public bool Active => Volatile.Read(ref active) != 0 && Volatile.Read(ref closed) == 0;
    public int Begin()
    {
        lock (gate) { if (Volatile.Read(ref closed) != 0) throw new ObjectDisposedException(nameof(SessionLifetime)); int next = Interlocked.Increment(ref generation); Volatile.Write(ref active, 1); return next; }
    }
    public bool IsCurrent(int session) => Active && Generation == session;
    public bool TryUse(int session, Action publish) { lock (gate) { if (!IsCurrent(session)) return false; publish(); return true; } }
    public void End() { lock (gate) { Volatile.Write(ref active, 0); Interlocked.Increment(ref generation); } }
    public void Close() { lock (gate) { Volatile.Write(ref closed, 1); End(); } }
}
internal sealed class RuntimeLifetime
{
    readonly object gate = new();
    int workers;
    bool closing;
    public int ActiveWorkers { get { lock (gate) return workers; } }
    public IDisposable Enter()
    {
        lock (gate) { if (closing) throw new ObjectDisposedException(nameof(RuntimeLifetime)); workers++; }
        return new ActionScope(() => { lock (gate) workers--; });
    }
    public bool TryBeginShutdown()
    {
        lock (gate) { if (closing) return false; closing = true; return workers == 0; }
    }
}
internal static class MediaFoundationUsage
{
    static readonly RuntimeLifetime runtime = new();
    public static int ActiveWorkers => runtime.ActiveWorkers;
    public static IDisposable Enter() => runtime.Enter();
    public static bool TryBeginShutdown() => runtime.TryBeginShutdown();
}


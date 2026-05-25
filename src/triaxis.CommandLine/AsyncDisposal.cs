namespace triaxis.CommandLine;

/// <summary>
/// <see cref="AsAsyncDisposable"/> returns an <see cref="IAsyncDisposable"/> for any
/// object — itself if it already implements the interface, otherwise an adapter that
/// forwards <c>DisposeAsync</c> to <c>IDisposable.Dispose</c>. Use with
/// <c>await using</c> to dispose <c>ServiceProvider</c>s that may hold
/// <see cref="IAsyncDisposable"/>-only singletons (which <c>ServiceProvider.Dispose()</c>
/// refuses to release synchronously).
/// </summary>
internal static class AsyncDisposal
{
    public static IAsyncDisposable AsAsyncDisposable(this object? target)
        => target as IAsyncDisposable ?? new SyncAdapter(target as IDisposable);

    private sealed class SyncAdapter(IDisposable? target) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            target?.Dispose();
            return default;
        }
    }
}

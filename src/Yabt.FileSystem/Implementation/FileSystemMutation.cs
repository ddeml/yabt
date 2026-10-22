namespace Yabt.FileSystem.Implementation;

internal static class FileSystemMutation
{
    // Cancellation may prevent a worker from starting, but must not abandon a
    // started worker while its caller still owns staging or an archive lock.
    // Return its actual result so successful commits and acquired resources
    // remain visible to the caller before it observes cancellation again.
    public static Task RunAsync(Action mutation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Factory.StartNew
        (
            mutation,
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
    }

    public static Task<T> RunAsync<T>(Func<T> mutation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Factory.StartNew
        (
            mutation,
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
    }
}

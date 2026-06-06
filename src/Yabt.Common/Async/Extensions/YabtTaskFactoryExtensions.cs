#pragma warning disable IDE0130 // Namespace does not match folder structure - Same as extended class System.Threading.Tasks.TaskFactory
namespace System.Threading.Tasks;
#pragma warning restore IDE0130 // Namespace does not match folder structure

/// <summary>Extension methods for the <see cref="TaskFactory"/>
/// </summary>
public static class YabtTaskFactoryExtensions
{
    /// <summary>
    /// Same as <see cref="Task.Run(Action, CancellationToken)"/>
    /// but with <see cref="TaskCreationOptions.LongRunning"/> configured
    /// and aborting immediately if the <paramref name="cancellationToken"/> is canceled,
    /// abandoning any result if the action/function has already been started.
    /// </summary>
    public static Task StartNewLongRunning
    (
        this TaskFactory taskFactory,
        Action action,
        Action<Exception>? abandonedExceptionHandler = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = taskFactory.StartNew
        (
            action,
            default,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
        return task.WaitOrAbandonAsync(abandonedExceptionHandler, cancellationToken);
    }

    /// <summary>
    /// Same as <see cref="Task.Run{TResult}(Func{TResult}, CancellationToken)"/>
    /// but with <see cref="TaskCreationOptions.LongRunning"/> configured.
    /// </summary>
    public static Task<T> StartNewLongRunning<T>
    (
        this TaskFactory taskFactory,
        Func<T> function,
        Action<T>? abandonedResultHandler = null,
        Action<Exception>? abandonedExceptionHandler = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = taskFactory.StartNew
        (
            function,
            default,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
        return task.WaitOrAbandonAsync
        (
            abandonedResultHandler,
            abandonedExceptionHandler,
            cancellationToken
        );
    }

    public static Task StartNewLongRunning
    (
        this TaskFactory taskFactory,
        Func<Task> function,
        Action<Exception>? abandonedExceptionHandler = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = taskFactory.StartNew
        (
            function,
            default,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        ).Unwrap();
        return task.WaitOrAbandonAsync(abandonedExceptionHandler, cancellationToken);
    }

    public static Task<T> StartNewLongRunning<T>
    (
        this TaskFactory taskFactory,
        Func<Task<T>> function,
        Action<T>? abandonedResultHandler = null,
        Action<Exception>? abandonedExceptionHandler = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = taskFactory.StartNew
        (
            function,
            default,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        ).Unwrap();
        return task.WaitOrAbandonAsync
        (
            abandonedResultHandler,
            abandonedExceptionHandler,
            cancellationToken
        );
    }

    public static Task StartNewLongRunning
    (
        this TaskFactory taskFactory,
        Action<object?> action,
        object? state,
        Action<Exception>? abandonedExceptionHandler = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = taskFactory.StartNew
        (
            action,
            state,
            default,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
        return task.WaitOrAbandonAsync(abandonedExceptionHandler, cancellationToken);
    }

    public static Task<T> StartNewLongRunning<T>
    (
        this TaskFactory taskFactory,
        Func<object?, T> function,
        object? state,
        Action<T>? abandonedResultHandler = null,
        Action<Exception>? abandonedExceptionHandler = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = taskFactory.StartNew
        (
            function,
            state,
            default,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
        return task.WaitOrAbandonAsync
        (
            abandonedResultHandler,
            abandonedExceptionHandler,
            cancellationToken
        );
    }

    public static Task StartNewLongRunning
    (
        this TaskFactory taskFactory,
        Func<object?, Task> function,
        object? state,
        Action<Exception>? abandonedExceptionHandler = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = taskFactory.StartNew
        (
            function,
            state,
            default,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        ).Unwrap();
        return task.WaitOrAbandonAsync(abandonedExceptionHandler, cancellationToken);
    }

    public static Task<T> StartNewLongRunning<T>
    (
        this TaskFactory taskFactory,
        Func<object?, Task<T>> function,
        object? state,
        Action<T>? abandonedResultHandler = null,
        Action<Exception>? abandonedExceptionHandler = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = taskFactory.StartNew
        (
            function,
            state,
            default,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        ).Unwrap();
        return task.WaitOrAbandonAsync
        (
            abandonedResultHandler,
            abandonedExceptionHandler,
            cancellationToken
        );
    }
}

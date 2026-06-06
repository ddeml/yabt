using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Yabt.Common.Async;

#pragma warning disable IDE0130 // Namespace does not match folder structure - Same as System.Threading.Tasks.Task
namespace System.Threading.Tasks;
#pragma warning restore IDE0130 // Namespace does not match folder structure

/// <summary>
/// Extension methods for the <see cref="Task"/> class
/// </summary>
public static class YabtTaskExtensions
{
    private static async Task ObserveAbandonedTaskAsync
    (
        Task task,
        Action<Exception>? abandonedExceptionHandler
    )
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            abandonedExceptionHandler?.Invoke(ex);
        }
    }

    private static async Task ObserveAbandonedTaskAsync<T>
    (
        Task<T> task,
        Action<T>? abandonedResultHandler,
        Action<Exception>? abandonedExceptionHandler
    )
    {
        try
        {
            var result = await task.ConfigureAwait(false);
            abandonedResultHandler?.Invoke(result);
        }
        catch (Exception ex)
        {
            abandonedExceptionHandler?.Invoke(ex);
        }
    }

    static YabtAsyncException CaptureOrCreateException(Task task, Exception ex)
    {
        var aggregateException = task.Exception;
        if (aggregateException is not null)
        {
            ExceptionDispatchInfo.Capture(aggregateException).Throw();
        }
        return new("Failed to capture task exception", ex);
    }

    /// <summary>
    /// Waits for a task until it completes or cancellation is requested.
    /// When cancellation wins, the task continues in the background and remains observed.
    /// </summary>
    public static async Task WaitOrAbandonAsync
    (
        this Task task,
        Action<Exception>? abandonedExceptionHandler = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return;
            }

            _ = ObserveAbandonedTaskAsync(task, abandonedExceptionHandler);
            throw;
        }
    }

    /// <summary>
    /// Waits for a task until it completes or cancellation is requested.
    /// When cancellation wins, the task continues in the background and its eventual result remains owned by
    /// <paramref name="abandonedResultHandler"/>.
    /// </summary>
    public static async Task<T> WaitOrAbandonAsync<T>
    (
        this Task<T> task,
        Action<T>? abandonedResultHandler = null,
        Action<Exception>? abandonedExceptionHandler = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (task.IsCompleted)
            {
                return await task.ConfigureAwait(false);
            }

            _ = ObserveAbandonedTaskAsync
            (
                task,
                abandonedResultHandler,
                abandonedExceptionHandler
            );
            throw;
        }
    }

    /// <summary>
    /// Captures all exceptions of the <paramref name="task"/>
    /// in an <see cref="AggregateException"/>
    /// instead of the default behavior that just returns the first occurring exception.
    /// </summary>
    [DebuggerStepThrough]
    public static async Task CaptureAllExceptions(this Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            throw CaptureOrCreateException(task, ex);
        }
    }

    ///<inheritdoc cref="CaptureAllExceptions(Task)"/>
    [DebuggerStepThrough]
    public static async Task<T> CaptureAllExceptions<T>(this Task<T> task)
    {
        try
        {
            return await task;
        }
        catch (Exception ex)
        {
            throw CaptureOrCreateException(task, ex);
        }
    }

    /// <summary>
    /// Captures all exceptions of the <paramref name="tasks"/>
    /// in an <see cref="AggregateException"/>
    /// instead of the default behavior that won't rethrow any exceptions at all.
    /// </summary>
    [DebuggerStepThrough]
    public static void CaptureAllExceptions(params IEnumerable<Task> tasks)
    {
        var aggregateExceptions = default(List<AggregateException>);
        foreach (var task in tasks)
        {
            var aggregateException = task.Exception;
            if (aggregateException is not null)
            {
                (aggregateExceptions ??= []).
                    Add(aggregateException);
            }
        }
        if (aggregateExceptions is not null)
        {
            throw new AggregateException(aggregateExceptions);
        }
    }

    ///<inheritdoc cref="CaptureAllExceptions(IEnumerable{Task})"/>
    [DebuggerStepThrough]
    public static void CaptureAllExceptions(Task task1, Task task2)
    {
        var aggregateException1 = task1.Exception;
        var aggregateException2 = task2.Exception;
        if (aggregateException1 is not null)
        {
            if (aggregateException2 is not null)
            {
                throw new AggregateException(aggregateException1, aggregateException2);
            }
            else
            {
                throw new AggregateException(aggregateException1);
            }
        }
        else if (aggregateException2 is not null)
        {
            throw new AggregateException(aggregateException2);
        }
    }

}

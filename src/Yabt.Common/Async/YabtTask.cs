namespace Yabt.Common.Async;

/// <summary>
/// Similar static methods as in the <see cref="Task"/> class
/// but with modified behavior.
/// </summary>
public static class YabtTask
{
    #region Run
    ///<inheritdoc cref="YabtTaskFactoryExtensions.StartNewLongRunning(TaskFactory, Action, CancellationToken)"/>
    public static Task Run
    (
        Action action,
        Action<Exception>? abandonedExceptionHandler = null,
        CancellationToken cancellationToken = default
    ) => Task.Factory.StartNewLongRunning
    (
        action,
        abandonedExceptionHandler,
        cancellationToken
    );

    ///<inheritdoc cref="YabtTaskFactoryExtensions.StartNewLongRunning{T}(TaskFactory, Func{T}, CancellationToken)"/>
    public static Task<T> Run<T>
    (
        Func<T> function,
        Action<T>? abandonedResultHandler = null,
        Action<Exception>? abandonedExceptionHandler = null,
        CancellationToken cancellationToken = default
    ) => Task.Factory.StartNewLongRunning
    (
        function,
        abandonedResultHandler,
        abandonedExceptionHandler,
        cancellationToken
    );

    ///<inheritdoc cref="YabtTaskFactoryExtensions.StartNewLongRunning(TaskFactory, Func{Task}, CancellationToken)"/>
    public static Task Run
    (
        Func<Task> function,
        Action<Exception>? abandonedExceptionHandler = null,
        CancellationToken cancellationToken = default
    ) => Task.Factory.StartNewLongRunning
    (
        function,
        abandonedExceptionHandler,
        cancellationToken
    );

    ///<inheritdoc cref="YabtTaskFactoryExtensions.StartNewLongRunning{T}(TaskFactory, Func{Task{T}}, CancellationToken)"/>
    public static Task<T> Run<T>
    (
        Func<Task<T>> function,
        Action<T>? abandonedResultHandler = null,
        Action<Exception>? abandonedExceptionHandler = null,
        CancellationToken cancellationToken = default
    ) => Task.Factory.StartNewLongRunning
    (
        function,
        abandonedResultHandler,
        abandonedExceptionHandler,
        cancellationToken
    );

    public static Task Run
    (
        Action<object?> action,
        object? state,
        Action<Exception>? abandonedExceptionHandler = null,
        CancellationToken cancellationToken = default
    ) => Task.Factory.StartNewLongRunning
    (
        action,
        state,
        abandonedExceptionHandler,
        cancellationToken
    );

    public static Task<T> Run<T>
    (
        Func<object?, T> function,
        object? state,
        Action<T>? abandonedResultHandler = null,
        Action<Exception>? abandonedExceptionHandler = null,
        CancellationToken cancellationToken = default
    ) => Task.Factory.StartNewLongRunning
    (
        function,
        state,
        abandonedResultHandler,
        abandonedExceptionHandler,
        cancellationToken
    );

    public static Task Run
    (
        Func<object?, Task> function,
        object? state,
            Action<Exception>? abandonedExceptionHandler = null,
        CancellationToken cancellationToken = default
    ) => Task.Factory.StartNewLongRunning
    (
        function,
        state,
        abandonedExceptionHandler,
        cancellationToken
    );

    public static Task<T> Run<T>
    (
        Func<object?, Task<T>> function,
        object? state,
        Action<T>? abandonedResultHandler = null,
        Action<Exception>? abandonedExceptionHandler = null,
        CancellationToken cancellationToken = default
    ) => Task.Factory.StartNewLongRunning
    (
        function,
        state,
        abandonedResultHandler,
        abandonedExceptionHandler,
        cancellationToken
    );

    #endregion

    #region WhenAll
    /// <summary>
    /// Same as <see cref="Task.WhenAll(IEnumerable{Task})"/>
    /// but using <see cref="YabtTaskExtensions.CaptureAllExceptions(Task)"/> for exception handling.
    /// </summary>
    public static Task WhenAll(IEnumerable<Task> tasks) =>
        Task.WhenAll(tasks).CaptureAllExceptions();

    /// <summary>
    /// Same as <see cref="Task.WhenAll(Task[])"/>
    /// but using <see cref="YabtTaskExtensions.CaptureAllExceptions(Task)"/> for exception handling.
    /// </summary>
    public static Task WhenAll(params Task[] tasks) =>
        Task.WhenAll(tasks).CaptureAllExceptions();

    /// <summary>
    /// Same as <see cref="Task.WhenAll{TResult}(IEnumerable{Task{TResult}})"/>
    /// but using <see cref="YabtTaskExtensions.CaptureAllExceptions{T}(Task{T})"/> for exception handling.
    /// </summary>
    public static Task<TResult[]> WhenAll<TResult>(IEnumerable<Task<TResult>> tasks) =>
        Task.WhenAll(tasks).CaptureAllExceptions();

    /// <summary>
    /// Same as <see cref="Task.WhenAll{TResult}(Task{TResult}[])"/>
    /// but using <see cref="YabtTaskExtensions.CaptureAllExceptions{T}(Task{T})"/> for exception handling.
    /// </summary>
    public static Task<TResult[]> WhenAll<TResult>(params Task<TResult>[] tasks) =>
        Task.WhenAll(tasks).CaptureAllExceptions();

    #endregion

    #region WhenAny
    /// <summary>
    /// Same as <see cref="Task.WhenAny(IEnumerable{Task})"/>
    /// but using <see cref="YabtTaskExtensions.CaptureAllExceptions(IEnumerable{Task})"/> for exception handling.
    /// </summary>
    public static async Task<Task> WhenAny(IEnumerable<Task> tasks)
    {
        var result = await Task.WhenAny(tasks);
        YabtTaskExtensions.CaptureAllExceptions(tasks);
        return result;
    }

    /// <summary>
    /// Same as <see cref="Task.WhenAny(Task[])"/>
    /// but using <see cref="YabtTaskExtensions.CaptureAllExceptions(Task[])"/> for exception handling.
    /// </summary>
    public static async Task<Task> WhenAny(params Task[] tasks)
    {
        var result = await Task.WhenAny(tasks);
        YabtTaskExtensions.CaptureAllExceptions(tasks);
        return result;
    }

    /// <summary>
    /// Same as <see cref="Task.WhenAny(Task, Task)"/>
    /// but using <see cref="YabtTaskExtensions.CaptureAllExceptions(Task, Task)"/> for exception handling.
    /// </summary>
    public static async Task<Task> WhenAny(Task task1, Task task2)
    {
        var result = await Task.WhenAny(task1, task2);
        YabtTaskExtensions.CaptureAllExceptions(task1, task2);
        return result;
    }

    /// <summary>
    /// Same as <see cref="Task.WhenAny{TResult}(IEnumerable{Task{TResult}})"/>
    /// but using <see cref="YabtTaskExtensions.CaptureAllExceptions(IEnumerable{Task})"/> for exception handling.
    /// </summary>
    public static async Task<Task<TResult>> WhenAny<TResult>(IEnumerable<Task<TResult>> tasks)
    {
        var result = await Task.WhenAny(tasks);
        YabtTaskExtensions.CaptureAllExceptions(tasks);
        return result;
    }

    /// <summary>
    /// Same as <see cref="Task.WhenAny{TResult}(Task{TResult}[])"/>
    /// but using <see cref="YabtTaskExtensions.CaptureAllExceptions(Task[])"/> for exception handling.
    /// </summary>
    public static async Task<Task<TResult>> WhenAny<TResult>(params Task<TResult>[] tasks)
    {
        var result = await Task.WhenAny(tasks);
        YabtTaskExtensions.CaptureAllExceptions(tasks);
        return result;
    }

    /// <summary>
    /// Same as <see cref="Task.WhenAny{TResult}(Task{TResult}, Task{TResult})"/>
    /// but using <see cref="YabtTaskExtensions.CaptureAllExceptions(Task, Task)"/> for exception handling.
    /// </summary>
    public static async Task<Task<TResult>> WhenAny<TResult>(Task<TResult> task1, Task<TResult> task2)
    {
        var result = await Task.WhenAny(task1, task2);
        YabtTaskExtensions.CaptureAllExceptions(task1, task2);
        return result;
    }

    #endregion

    #region WaitAny
    /// <summary>
    /// Same as <see cref="Task.WaitAny(Task[])"/>
    /// but using <see cref="YabtTaskExtensions.CaptureAllExceptions(Task[])"/> for exception handling.
    /// </summary>
    public static int WaitAny(params Task[] tasks)
    {
        var result = Task.WaitAny(tasks);
        YabtTaskExtensions.CaptureAllExceptions(tasks);
        return result;
    }

    /// <summary>
    /// Same as <see cref="Task.WaitAny(Task[])"/>
    /// but using <see cref="YabtTaskExtensions.CaptureAllExceptions(Task[])"/> for exception handling.
    /// </summary>
    public static int WaitAny(Task[] tasks, CancellationToken cancellationToken)
    {
        var result = Task.WaitAny(tasks, cancellationToken);
        YabtTaskExtensions.CaptureAllExceptions(tasks);
        return result;
    }

    /// <summary>
    /// Same as <see cref="Task.WaitAny(Task[], int)"/>
    /// but using <see cref="YabtTaskExtensions.CaptureAllExceptions(Task[])"/> for exception handling.
    /// </summary>
    public static int WaitAny(Task[] tasks, int millisecondsTimeout)
    {
        var result = Task.WaitAny(tasks, millisecondsTimeout);
        YabtTaskExtensions.CaptureAllExceptions(tasks);
        return result;
    }

    /// <summary>
    /// Same as <see cref="Task.WaitAny(Task[], TimeSpan)"/>
    /// but using <see cref="YabtTaskExtensions.CaptureAllExceptions(Task[])"/> for exception handling.
    /// </summary>
    public static int WaitAny(Task[] tasks, TimeSpan timeout)
    {
        var result = Task.WaitAny(tasks, timeout);
        YabtTaskExtensions.CaptureAllExceptions(tasks);
        return result;
    }

    /// <summary>
    /// Same as <see cref="Task.WaitAny(Task[], int, CancellationToken)"/>
    /// but using <see cref="YabtTaskExtensions.CaptureAllExceptions(Task[])"/> for exception handling.
    /// </summary>
    public static int WaitAny(Task[] tasks, int millisecondsTimeout, CancellationToken cancellationToken)
    {
        var result = Task.WaitAny(tasks, millisecondsTimeout, cancellationToken);
        YabtTaskExtensions.CaptureAllExceptions(tasks);
        return result;
    }

    #endregion

}

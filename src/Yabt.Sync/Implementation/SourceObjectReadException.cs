namespace Yabt.Sync.Implementation;

internal sealed class SourceObjectReadException : YabtSyncException
{
    public SourceObjectReadException
    (
        string sourcePath,
        string objectKey,
        Exception innerException
    ) : base
    (
        $"Could not read source object '{sourcePath}' (object key '{objectKey}'): " +
            innerException.Message,
        innerException
    )
    {
        SourcePath = sourcePath;
        ObjectKey = objectKey;
        Reason = innerException.Message;
    }

    public string SourcePath { get; }

    public string ObjectKey { get; }

    public string Reason { get; }

    public static IReadOnlyList<SourceObjectReadException> FindAll(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var failures = new List<SourceObjectReadException>();
        var visitedExceptions = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var failureKeys = new HashSet<string>(StringComparer.Ordinal);
        var pendingExceptions = new Stack<Exception>();
        pendingExceptions.Push(exception);

        while (pendingExceptions.TryPop(out var currentException))
        {
            if (!visitedExceptions.Add(currentException))
            {
                continue;
            }

            if (currentException is SourceObjectReadException sourceReadException &&
                failureKeys.Add(sourceReadException.ObjectKey))
            {
                failures.Add(sourceReadException);
            }

            if (currentException is AggregateException aggregateException)
            {
                for (var index = aggregateException.InnerExceptions.Count - 1; index >= 0; index--)
                {
                    pendingExceptions.Push(aggregateException.InnerExceptions[index]);
                }

                continue;
            }

            if (currentException.InnerException is not null)
            {
                pendingExceptions.Push(currentException.InnerException);
            }
        }

        return failures;
    }

    public static bool TryFind
    (
        Exception exception,
        out SourceObjectReadException? sourceReadException
    )
    {
        sourceReadException = FindAll(exception).FirstOrDefault();
        return sourceReadException is not null;
    }
}

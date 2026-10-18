namespace Yabt.Core;

/// <summary>
/// Marks detected overlap between a source read scope and storage that the operation may
/// mutate. Unlike a recoverable source read failure, this requires the operation to stop.
/// Providers may wrap it with their own location and operation context.
/// </summary>
public sealed class ObjectStoreOverlapException(string message) : YabtCoreException(message)
{
    /// <summary>Recognizes overlap evidence through provider, projection, and aggregate wrappers.</summary>
    public static bool IsContainedIn(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) { continue; }
            if (current is ObjectStoreOverlapException) { return true; }
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) { pending.Push(inner); }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
        return false;
    }
}

#pragma warning disable IDE0130 // Namespace does not match folder structure - Same as extended interface System.Collections.Generic.IEnumerable<T>
namespace System.Collections.Generic;
#pragma warning restore IDE0130 // Namespace does not match folder structure

public static class YabtEnumerableExtensions
{
    /// <summary>
    /// Same as <see cref="Enumerable.TryGetNonEnumeratedCount"/> but also supporting <see cref="IReadOnlyCollection{T}"/> implementations.
    /// </summary>
    public static bool YabtTryGetNonEnumeratedCount<TSource>(this IEnumerable<TSource> source, out int count)
    {
        if (source.TryGetNonEnumeratedCount(out count))
        {
            return true;
        }
        if (source is IReadOnlyCollection<TSource> readOnlyCollection)
        {
            count = readOnlyCollection.Count;
            return true;
        }
        return false;
    }

}

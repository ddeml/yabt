using System.Text.Json;
using Yabt.Common.Implementation;

namespace Yabt.Common;

/// <summary>
/// Owns disposable, disk-backed operation evidence. All collections created by
/// one index share its database and bounded page cache; none is durable state.
/// </summary>
public abstract class TemporaryIndex : IDisposable, IAsyncDisposable
{
    internal static readonly AsyncLocal<TemporaryIndexOperationScope?> ActiveOperation = new();

    public const int PageCacheBytes = 2 * 1024 * 1024;

    public static TemporaryIndex Create(TemporaryContentOptions? options = null) =>
        ActiveOperation.Value is { } operation
            ? operation.CreateLease()
            : new TemporaryIndexLease(new SqliteTemporaryIndex(options ?? new()));

    /// <summary>
    /// Shares one temporary database among this operation's manifest owners and
    /// nested projections. Owners may outlive the scope through their leases.
    /// </summary>
    public static IDisposable BeginOperation(TemporaryContentOptions? options = null)
    {
        var scope = new TemporaryIndexOperationScope(options ?? new(), ActiveOperation.Value);
        ActiveOperation.Value = scope;
        return scope;
    }

    public abstract DiskDictionary<T> CreateDictionary<T>
    (
        JsonSerializerOptions? options = null,
        IEqualityComparer<string>? comparer = null
    );

    public abstract DiskEntryCollection<T> CreateCollection<T>(JsonSerializerOptions? options = null);

    public DiskSet CreateSet(IEqualityComparer<string>? comparer = null) => new(CreateDictionary<byte>(comparer: comparer));

    public abstract void Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

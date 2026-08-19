using Yabt.Core.Implementation;

namespace Yabt.Core.Abstractions;

/// <summary>
/// Adds guarded archive mutations to an object store. The expected content hash is always the
/// canonical YABT <see cref="Models.ArchiveHash"/> of the current object's complete bytes.
/// </summary>
public interface IArchiveMutableObjectStore : IObjectStore, IArchiveMutationLockProvider
{
    /// <summary>
    /// Replaces an existing object only when its complete current bytes match
    /// <paramref name="expectedContentHash"/>.
    /// </summary>
    /// <returns><see langword="true"/> when replaced; otherwise <see langword="false"/>.</returns>
    Task<bool> TryReplaceIfContentHashMatchesAsync
    (
        string key,
        string expectedContentHash,
        Stream replacementContent,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Deletes an existing object only when its complete current bytes match
    /// <paramref name="expectedContentHash"/>.
    /// </summary>
    /// <returns><see langword="true"/> when deleted; otherwise <see langword="false"/>.</returns>
    Task<bool> TryDeleteIfContentHashMatchesAsync
    (
        string key,
        string expectedContentHash,
        CancellationToken cancellationToken = default
    );

    Task<IArchiveMutationLock> IArchiveMutationLockProvider.AcquireArchiveMutationLockAsync
    (
        CancellationToken cancellationToken
    ) => ArchiveMutationLock.AcquireAsync(this, cancellationToken);
}

namespace Yabt.Core.Abstractions;

public interface IArchiveMutationLockProvider
{
    /// <summary>
    /// Acquires exclusive mutation ownership. The optional runtime limit bounds remote JSON
    /// lock documents; providers with native locks do not deserialize those documents.
    /// </summary>
    Task<IArchiveMutationLock> AcquireArchiveMutationLockAsync
    (
        CancellationToken cancellationToken = default,
        long? maximumDocumentBytes = null
    );
}

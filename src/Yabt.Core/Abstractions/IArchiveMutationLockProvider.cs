namespace Yabt.Core.Abstractions;

public interface IArchiveMutationLockProvider
{
    Task<IArchiveMutationLock> AcquireArchiveMutationLockAsync
    (
        CancellationToken cancellationToken = default
    );
}

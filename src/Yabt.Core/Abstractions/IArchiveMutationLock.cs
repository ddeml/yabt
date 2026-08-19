namespace Yabt.Core.Abstractions;

/// <summary>
/// Represents exclusive permission for one YABT process to mutate an archive.
/// </summary>
public interface IArchiveMutationLock : IAsyncDisposable
{
    /// <summary>
    /// Is cancelled if the lock can no longer be renewed or its ownership is lost.
    /// Long-running mutations should link this token with their operation token.
    /// </summary>
    CancellationToken LockLostToken { get; }
}

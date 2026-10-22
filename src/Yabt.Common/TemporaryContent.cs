namespace Yabt.Common;

/// <summary>
/// A seekable construction stream that spills to disk. Seal it before opening
/// independent readers. Disposing the owner prevents new readers; existing
/// readers retain the immutable backing until they are disposed.
/// Construction operations must not run concurrently with each other or disposal.
/// </summary>
public abstract class TemporaryContent : Stream
{
    public abstract bool IsSealed { get; }

    public abstract bool IsFileBacked { get; }

    public abstract ValueTask SealAsync(CancellationToken cancellationToken = default);

    public abstract Stream OpenRead();
}

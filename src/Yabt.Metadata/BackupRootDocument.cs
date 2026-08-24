using Yabt.Core.Models;

namespace Yabt.Metadata;

/// <summary>
/// A validated backup-root descriptor together with the exact bytes from which it was read.
/// </summary>
public sealed class BackupRootDocument
{
    private readonly byte[] _content;

    internal BackupRootDocument
    (
        BackupRootDescriptor descriptor,
        byte[] content
    )
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(content);

        Descriptor = descriptor;
        _content = content;
        ContentHash = ArchiveHash.Compute(content);
    }

    public BackupRootDescriptor Descriptor { get; }

    public long ContentLength => _content.LongLength;

    public string ContentHash { get; }

    /// <summary>
    /// Opens a read-only stream over the original descriptor bytes. The returned stream does not
    /// expose its backing buffer, so callers cannot alter this document's immutable snapshot.
    /// </summary>
    public Stream OpenRead() => new MemoryStream
    (
        _content,
        0,
        _content.Length,
        writable: false,
        publiclyVisible: false
    );

    public async Task CopyToAsync
    (
        Stream destination,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(destination);

        await destination.WriteAsync(_content, cancellationToken);
    }

    /// <summary>
    /// Compares exact serialized bytes, including whitespace, property order, and a UTF-8 byte
    /// order mark. Descriptor value equality is intentionally insufficient for byte-for-byte
    /// carry-over.
    /// </summary>
    public bool ContentEquals(BackupRootDocument? other) =>
        other is not null && _content.AsSpan().SequenceEqual(other._content);
}

using System.Buffers;
using Yabt.Common.Implementation;

namespace Yabt.Common;

/// <summary>
/// Reads deliberately small configuration or control documents with a byte limit.
/// File-count-dependent manifests and ordinary payloads must not use this helper.
/// </summary>
public static class SmallDocument
{
    public static async Task WriteAsync
    (
        Stream destination,
        long maximumBytes,
        string documentName,
        Func<Stream, CancellationToken, Task> writeDocument,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(writeDocument);
        ValidateLimit(maximumBytes, documentName);

        using var buffer = new SmallDocumentWriteBuffer(maximumBytes, documentName);
        await writeDocument(buffer, cancellationToken);
        buffer.Position = 0;
        await buffer.CopyToAsync(destination, cancellationToken);
    }

    public static async Task<byte[]> ReadAllBytesAsync
    (
        Stream source,
        long maximumBytes,
        string documentName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateLimit(maximumBytes, documentName);

        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(81_920, maximumBytes + 1));
        try
        {
            using var content = new MemoryStream();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Probe at most one byte past the limit, including when Length is unavailable
                // or inaccurate. Trailing whitespace is part of the document's byte count.
                var readLength = (int)Math.Min(buffer.Length, maximumBytes - content.Length + 1);
                var read = await source.ReadAsync(buffer.AsMemory(0, readLength), cancellationToken);
                if (read == 0) { return content.ToArray(); }
                if (read > maximumBytes - content.Length)
                {
                    throw new InvalidDataException
                    (
                        $"Document '{documentName}' exceeds its maximum size of {maximumBytes} bytes."
                    );
                }

                content.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ValidateLimit(long maximumBytes, string documentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);
        if (maximumBytes <= 0 || maximumBytes > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException
            (
                nameof(maximumBytes),
                $"A small document limit must be between 1 and {Array.MaxLength} bytes."
            );
        }
    }
}

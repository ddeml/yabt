namespace Yabt.Format.Zip.Implementation;

/// <summary>
/// One central-directory record. Callers may persist records in an operation's
/// temporary index instead of retaining the complete directory in memory.
/// </summary>
internal sealed record ZipEntryRecord
(
    string FullName,
    long Length,
    long CompressedLength,
    long LocalHeaderOffset,
    long CentralDirectoryOffset,
    uint Crc32,
    ushort CompressionMethod,
    ushort Flags,
    int ExternalAttributes,
    DateTimeOffset LastWriteTime
)
{
    public string Name => FullName[(FullName.LastIndexOf('/') + 1)..];
}

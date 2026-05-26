namespace Yabt.Core.Models;

public sealed record ArchiveManifest
(
    string SourcePath,
    DateTimeOffset CreatedAtUtc,
    ArchiveFormat ArchiveFormat,
    IEnumerable<ManifestFileEntry> Files,
    long TotalBytes,
    string ManifestHash,
    string? PackageName
);

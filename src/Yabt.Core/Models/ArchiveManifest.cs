namespace Yabt.Core.Models;

public sealed record ArchiveManifest
(
    string SourcePath,
    DateTimeOffset CreatedAtUtc,
    string Format,
    IEnumerable<ManifestFileEntry> Files,
    long TotalBytes,
    string ManifestHash,
    string PackageName
);

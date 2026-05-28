namespace Yabt.Core.Models;

public sealed record ManifestFileEntry
(
    string RelativePath,
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    string? ContentHash = default
);

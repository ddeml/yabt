namespace Yabt.Core.Models;

public sealed record ArchiveObjectInfo
(
    ArchiveObjectKey Key,
    long? ContentLength = default,
    DateTimeOffset? LastModifiedUtc = default,
    string? ContentHash = default
);

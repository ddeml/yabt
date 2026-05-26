namespace Yabt.Core.Models;

public sealed record ArchiveObjectInfo
(
    ArchiveObjectKey Key,
    long? ContentLength,
    DateTimeOffset? LastModifiedUtc,
    string? ContentHash
);

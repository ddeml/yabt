namespace Yabt.Core.Models;

public sealed record ArchiveObjectInfo
(
    string Key,
    long? ContentLength = default,
    DateTimeOffset? LastModifiedUtc = default,
    string? ContentHash = default,
    string? ChangeFingerprint = default
);

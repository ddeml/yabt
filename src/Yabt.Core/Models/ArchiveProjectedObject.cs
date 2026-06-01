namespace Yabt.Core.Models;

public sealed record ArchiveProjectedObject
(
    string RelativePath,
    Func<CancellationToken, Task<ArchiveObjectContent>> OpenContentAsync,
    long? ContentLength = default,
    DateTimeOffset? LastModifiedUtc = default,
    string? ContentHash = default
);

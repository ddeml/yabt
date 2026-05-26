namespace Yabt.Core.Models;

public sealed record ChangeDetectionRequest
(
    string SourceRoot,
    DateTimeOffset? SinceUtc,
    string? SnapshotToken
);

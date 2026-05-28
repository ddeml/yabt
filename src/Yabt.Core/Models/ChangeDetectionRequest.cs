namespace Yabt.Core.Models;

public sealed record ChangeDetectionRequest
(
    string SourceRoot,
    DateTimeOffset? SinceUtc = default,
    string? SnapshotToken = default
);

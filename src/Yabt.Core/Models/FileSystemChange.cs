namespace Yabt.Core.Models;

public sealed record FileSystemChange
(
    ChangeKind Kind,
    string SourcePath,
    string RelativePath,
    long? Length = default,
    DateTimeOffset? LastWriteTimeUtc = default,
    string? PreviousRelativePath = default
);

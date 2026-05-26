namespace Yabt.Core.Models;

public sealed record FileSystemChange
(
    ChangeKind Kind,
    string SourcePath,
    string RelativePath,
    long? Length,
    DateTimeOffset? LastWriteTimeUtc,
    string? PreviousRelativePath
);

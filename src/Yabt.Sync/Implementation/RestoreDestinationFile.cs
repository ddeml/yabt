namespace Yabt.Sync.Implementation;

internal sealed record RestoreDestinationFile
(
    string RelativePath,
    string ArchiveKey,
    long? ContentLength,
    DateTimeOffset? LastModifiedUtc
);

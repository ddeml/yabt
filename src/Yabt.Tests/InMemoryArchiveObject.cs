namespace Yabt.Tests;

public sealed record InMemoryArchiveObject
(
    string Key,
    ReadOnlyMemory<byte> Content,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset LastModifiedUtc
);

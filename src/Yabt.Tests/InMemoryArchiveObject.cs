using Yabt.Core.Models;

namespace Yabt.Tests;

public sealed record InMemoryArchiveObject
(
    ArchiveObjectKey Key,
    ReadOnlyMemory<byte> Content,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset LastModifiedUtc
);

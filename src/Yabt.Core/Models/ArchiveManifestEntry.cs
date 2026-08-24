using System.Text.Json.Serialization;

namespace Yabt.Core.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveManifestEntry
(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] string RelativePath,
    [property: JsonRequired] string StoredPath,
    [property: JsonRequired] long Length,
    [property: JsonRequired] DateTimeOffset LastModifiedUtc,
    [property: JsonRequired] string ContentHash,
    ArchiveProjectionProvenance? Projection = default
);

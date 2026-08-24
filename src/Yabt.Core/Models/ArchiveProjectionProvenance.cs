using System.Text.Json.Serialization;

namespace Yabt.Core.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveProjectionProvenance
(
    [property: JsonRequired] string LogicalPath,
    [property: JsonRequired] string Format,
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] string ProjectionId,
    [property: JsonRequired] string ArtifactRole
);

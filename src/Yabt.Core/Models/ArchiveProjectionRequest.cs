using Yabt.Core.Abstractions;

namespace Yabt.Core.Models;

public sealed record ArchiveProjectionRequest
(
    IObjectStore SourceStore,
    string? SourcePrefix = default,
    FolderPolicy? Policy = default,
    string? SourceDisplayName = default
);

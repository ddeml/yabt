using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Sync;

public sealed record SyncRunRequest
(
    string SourceRoot,
    bool DryRun = default,
    IObjectStore? SourceStore = default,
    IObjectStore? TargetStore = default,
    BackupRootDescriptor? SourceDescriptor = default,
    BackupRootDescriptor? TargetDescriptor = default,
    FolderPolicy? Policy = default
);

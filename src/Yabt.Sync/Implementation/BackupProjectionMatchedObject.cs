using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed record BackupProjectionMatchedObject
(
    ArchiveProjectedObject ProjectedObject,
    string RelativePath,
    ArchiveObjectInfo TargetObject,
    ArchiveChangeManifestEntry? PreviousManifestEntry
);

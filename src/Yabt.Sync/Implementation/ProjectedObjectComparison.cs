using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed record ProjectedObjectComparison
(
    bool Same,
    ArchiveChangeManifestEntry? ManifestEntry,
    ArchiveObjectContent? PreparedContent
);

using Yabt.Core.Models;

namespace Yabt.Packaging;

public sealed record ArchivePackageResult
(
    string ArchivePath,
    string ManifestPath,
    ArchiveManifest Manifest
);

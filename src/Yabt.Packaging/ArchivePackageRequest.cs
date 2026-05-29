using Yabt.Core.Models;

namespace Yabt.Packaging;

public sealed record ArchivePackageRequest
(
    string SourceDirectory,
    string OutputDirectory,
    FolderPolicy Policy,
    string Format,
    string PackageExtension,
    DateTimeOffset CreatedAtUtc
);

using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Metadata;

/// <summary>Resolves user location inputs and owns native location and traversal rules.</summary>
public interface IArchiveLocationResolver
{
    Task<ArchiveSourceLocation> LocateSourceAsync
    (
        string input,
        bool requireRoot = false,
        CancellationToken cancellationToken = default
    );

    void ConfigureSourceTraversal(ArchiveSourceLocation source, IObjectStore targetStore);

    Task<FolderPolicy> ReadPolicyAsync
    (
        ArchiveSourceLocation source,
        ObjectPath prefix,
        CancellationToken cancellationToken = default
    );

    void ValidateRestoreLocations
    (
        ArchiveSourceLocation descriptorLocation,
        IRestoreDestination destination,
        IObjectStore archiveStore
    );
}

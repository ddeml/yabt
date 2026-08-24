using Yabt.Core.Models;

namespace Yabt.Metadata;

public sealed record BackupRootLocation
(
    string RootPath,
    BackupRootDescriptor Descriptor,
    BackupRootDocument? Document = default
)
{
    public BackupRootLocation
    (
        string rootPath,
        BackupRootDocument document
    ) : this(rootPath, document.Descriptor, document)
    {
    }
}

using Yabt.Core.Models;

namespace Yabt.Metadata;

public sealed record BackupRootLocation
(
    string RootPath,
    BackupRootDescriptor Descriptor
);

using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Metadata;

/// <summary>A provider-resolved source selection with canonical paths within its store.</summary>
public sealed record ArchiveSourceLocation
(
    BackupRootLocation Root,
    ObjectPath SourcePrefix,
    ObjectPath SelectionPrefix,
    string DisplayName,
    IObjectStore SourceStore
);

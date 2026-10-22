using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

internal sealed class TargetFolderState
{
    public Dictionary<string, ArchiveObjectInfo> Objects { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Folders { get; } = new(StringComparer.Ordinal);
}

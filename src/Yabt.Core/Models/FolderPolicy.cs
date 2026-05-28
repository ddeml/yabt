namespace Yabt.Core.Models;

public sealed record FolderPolicy
(
    PackageMode Mode = PackageMode.Mirror,
    ArchiveFormat? Format = default,
    IEnumerable<string>? IncludePatterns = default,
    IEnumerable<string>? ExcludePatterns = default
)
{
    public static FolderPolicy Default { get; } = new();
}

namespace Yabt.Core.Models;

public sealed record FolderPolicy
(
    PackageMode Mode,
    ArchiveFormat? Format,
    IEnumerable<string> IncludePatterns,
    IEnumerable<string> ExcludePatterns
)
{
    public static FolderPolicy Default { get; } = new
    (
        PackageMode.Mirror,
        null,
        [],
        []
    );
}

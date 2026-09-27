namespace Yabt.Core.Models;

public sealed record FolderPolicy
(
    string Format = "mirror",
    IEnumerable<string>? IncludePatterns = default,
    IEnumerable<string>? ExcludePatterns = default,
    object? Options = default,
    IReadOnlyDictionary<string, DirectoryLinkPolicy>? DirectoryLinks = null
)
{
    public static FolderPolicy Default { get; } = new();
}

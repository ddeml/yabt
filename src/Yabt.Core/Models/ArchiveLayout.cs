namespace Yabt.Core.Models;

public sealed record ArchiveLayout
(
    string LivePrefix = "live",
    string HistPrefix = "hist"
)
{
    public static ArchiveLayout Default { get; } = new();
}

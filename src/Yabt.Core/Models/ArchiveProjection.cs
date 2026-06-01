namespace Yabt.Core.Models;

public sealed record ArchiveProjection
(
    IEnumerable<ArchiveProjectedObject> Objects
)
{
    public static ArchiveProjection Empty { get; } = new([]);
}

namespace Yabt.Packaging;

public interface IArchivePackageBuilder
{
    Task<ArchivePackageResult> BuildAsync
    (
        ArchivePackageRequest request,
        CancellationToken cancellationToken = default
    );
}

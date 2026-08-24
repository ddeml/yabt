using Yabt.Core.Models;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class ArchiveRestoreRequestTests
{
    [TestMethod]
    public void GroupedRequestExposesArtifactsAndRejectsSingleArtifactView()
    {
        var package = CreateArtifact("folder.zip");
        var manifest = CreateArtifact("folder.zip.yabt-manifest.json");

        var request = new ArchiveRestoreRequest
        (
            [package, manifest],
            restoreAsRoot: true,
            requireCompleteProjection: true
        );

        Assert.HasCount(2, request.Artifacts);
        Assert.AreSame(package, request.Artifacts[0]);
        Assert.AreSame(manifest, request.Artifacts[1]);
        Assert.IsTrue(request.RestoreAsRoot);
        Assert.IsTrue(request.RequireCompleteProjection);
        var exception = Assert.Throws<InvalidOperationException>(() => _ = request.Artifact);
        StringAssert.Contains(exception.Message, "single-artifact view is unavailable");
    }

    private static ArchiveProjectedObject CreateArtifact(string relativePath) => new
    (
        relativePath,
        cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ArchiveObjectContent(new MemoryStream()));
        }
    );
}

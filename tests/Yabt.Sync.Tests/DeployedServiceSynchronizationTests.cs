using Yabt.Sync.Tests.Implementation;
using Yabt.Tests;

namespace Yabt.Sync.Tests;

[TestClass]
[TestCategory("DeployedService")]
[DoNotParallelize]
public sealed class DeployedServiceSynchronizationTests
{
    [TestMethod]
    [DataRow("azureBlob", "mirror")]
    [DataRow("azureBlob", "zip")]
    [DataRow("webDav", "mirror")]
    [DataRow("webDav", "zip")]
    public async Task BackupRestorePreservesContentAndHistory(string providerKind, string format)
    {
        if (!DeployedServiceFixture.IsEnabled)
        {
            Assert.Inconclusive("Deployed-service acceptance requires explicit opt-in and private settings.");
        }

        await DeployedServiceFixture.RunAsync
        (
            providerKind,
            "synchronization-" + format,
            fixture => DeployedSynchronizationScenario.ExecuteAsync(fixture.Store, providerKind, format)
        );
    }
}

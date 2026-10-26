using Yabt.Sync.Tests.Implementation;
using Yabt.Tests;

namespace Yabt.Sync.Tests;

[TestClass]
[TestCategory("DeployedService")]
[DoNotParallelize]
public sealed class DeployedServiceSynchronizationTests
{
    [TestMethod]
    [DataRow("mirror")]
    [DataRow("zip")]
    public Task AzureBackupRestorePreservesContentAndHistory(string format) =>
        RunSynchronizationAsync("azureBlob", format);

    [TestMethod]
    [DataRow("mirror")]
    [DataRow("zip")]
    public Task WebDavBackupRestorePreservesContentAndHistory(string format) =>
        RunSynchronizationAsync("webDav", format);

    private static async Task RunSynchronizationAsync(string providerKind, string format)
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

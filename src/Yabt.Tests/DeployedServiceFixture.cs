using Yabt.Core.Abstractions;
using Yabt.Tests.Implementation;

namespace Yabt.Tests;

/// <summary>
/// Explicitly opted-in deployed-service tests own one unique remote subtree.
/// Provider credentials and raw failures never become test-runner output.
/// </summary>
public sealed class DeployedServiceFixture
{
    private DeployedServiceFixture(IArchiveMutableObjectStore store)
    {
        Store = store;
    }

    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable("YABT_SERVICE_ACCEPTANCE") == "1" &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YABT_SERVICE_ACCEPTANCE_SETTINGS"));

    public IArchiveMutableObjectStore Store { get; }

    public static async Task RunAsync
    (
        string providerKind,
        string scenario,
        Func<DeployedServiceFixture, Task> test
    )
    {
        ArgumentNullException.ThrowIfNull(test);
        if (!IsEnabled)
        {
            throw new YabtTestsException("Deployed-service acceptance requires explicit opt-in and a private settings path.");
        }
        if (providerKind is not ("azureBlob" or "webDav") ||
            scenario.Length is < 1 or > 80 ||
            scenario.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new YabtTestsException("Invalid deployed-service case identifier.");
        }

        var caseId = $"{providerKind}-{scenario}-{Guid.NewGuid():N}";
        var phase = "configuration";
        Exception? failure = null;
        DeployedServiceStore? deployed = null;
        try
        {
            var configuration = await DeployedServiceConfiguration.LoadAsync(providerKind, caseId);
            phase = "isolation-preflight";
            deployed = providerKind == "azureBlob" ?
                await DeployedAzureService.CreateAsync(configuration) :
                await DeployedWebDavService.CreateAsync(configuration);
            phase = "initialization";
            await deployed.Store.InitializeAsync();
            await deployed.Store.EnsureReadyAsync();
            phase = "test";
            await test(new(deployed.Store));
            phase = "cleanup";
            using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await deployed.CleanupAsync(cleanupCancellation.Token);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (deployed is not null)
            {
                try
                {
                    await deployed.DisposeAsync();
                }
                catch (Exception exception)
                {
                    failure = failure is null ? exception : new AggregateException(failure, exception);
                    phase += "/dispose";
                }
            }
        }

        if (failure is not null)
        {
            var diagnostic = await DeployedServiceDiagnostics.RecordFailureAsync(caseId, phase, failure);
            throw new YabtTestsException
            (
                $"Deployed-service case {caseId} failed during {phase}. " +
                $"{diagnostic} Any unfinished remote fixture was retained; no base folder or container was deleted."
            );
        }
    }
}

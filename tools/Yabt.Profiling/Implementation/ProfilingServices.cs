using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Yabt.Profiling.Implementation;

internal static class ProfilingServices
{
    internal static ServiceProvider Create(string scratchRoot)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection
        (
            new Dictionary<string, string?>
            {
                ["TemporaryContent:TemporaryDirectory"] = scratchRoot,
                ["Sync:RestoreMaximumConcurrency"] = "5",
            }
        ).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtMetadata();
        services.AddYabtFileSystemObjectStore();
        services.AddYabtMirrorFormatHandler();
        services.AddYabtZipFormatHandler();
        services.AddYabtTemporaryContent("TemporaryContent");
        services.AddYabtSync("Sync");
        return services.BuildServiceProvider();
    }
}

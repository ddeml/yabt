using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Yabt.Profiling.Implementation;

internal static class ProfilingServices
{
    internal static ServiceProvider Create
    (
        string scratchRoot,
        string? progressDirectory = null,
        bool? progressEnabled = null,
        ResumeProfilingObserver? observer = null
    )
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection
        (
            new Dictionary<string, string?>
            {
                ["TemporaryContent:TemporaryDirectory"] = scratchRoot,
                ["Sync:RestoreMaximumConcurrency"] = "5",
                ["Sync:ProgressDirectory"] = progressDirectory,
                ["Sync:ProgressEnabled"] = progressEnabled?.ToString(),
            }
        ).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        if (observer is null)
        {
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        }
        else
        {
            services.AddSingleton(observer);
            services.AddSingleton(typeof(ILogger<>), typeof(ResumeProfilingLogger<>));
        }
        services.AddYabtMetadata();
        services.AddYabtFileSystemObjectStore();
        services.AddYabtMirrorFormatHandler();
        services.AddYabtZipFormatHandler();
        services.AddYabtTemporaryContent("TemporaryContent");
        services.AddYabtSync("Sync");
        return services.BuildServiceProvider();
    }
}

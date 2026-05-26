using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yabt.AzureBlob;
using Yabt.Metadata;
using Yabt.Packaging;
using Yabt.Sync;

namespace Yabt.Cli;

public static class CliProgram
{
    public static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        builder.Services
            .AddAzureBlobArchiveStore("AzureBlobArchive")
            .AddYabtMetadata()
            .AddYabtPackaging()
            .AddYabtSync()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<CommandRunner>();

        using var host = builder.Build();
        var runner = host.Services.GetRequiredService<CommandRunner>();
        return await runner.RunAsync(args);
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yabt.Cli.Implementation;
using Yabt.Metadata;
using Yabt.Packaging;

namespace Yabt.Cli;

public static class CliProgram
{
    public static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        builder.Services
            .AddYabtFileSystemObjectStore("ObjectStores:FileSystem")
            .AddYabtAzureBlobObjectStore("ObjectStores:AzureBlob")
            .AddYabtWebDavObjectStore("ObjectStores:WebDav")
            .AddYabtMirrorFormatProvider()
            .AddYabtZipFormatProvider("Formats:Zip")
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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yabt.Cli.Implementation;
using Yabt.Common;

namespace Yabt.Cli;

public static class CliProgram
{
    public static async Task<int> Main(string[] args)
    {
        LogFileCommandLineResult commandLine;
        LogFileStartup logFileStartup;
        try
        {
            commandLine = LogFileCommandLine.Parse(args);
            logFileStartup = LogFileStartup.Prepare(commandLine.Request);
        }
        catch (YabtException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        using var logFileLifetime = logFileStartup;
        var commandArguments = commandLine.RemainingArguments;
        var builder = Host.CreateApplicationBuilder(commandArguments);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        if (logFileStartup.IsEnabled)
        {
            logFileStartup.Attach(builder.Logging);
            builder.Logging.AddFilter<YabtFileLoggerProvider>
            (
                static level => level is >= LogLevel.Debug and < LogLevel.None
            );
        }

        builder.Services
            .AddYabtFileSystemObjectStore("ObjectStores:FileSystem")
            .AddYabtAzureBlobObjectStore()
            .AddYabtWebDavObjectStore("ObjectStores:WebDav")
            .AddYabtMirrorFormatHandler()
            .AddYabtZipFormatHandler("Formats:Zip")
            .AddYabtMetadata()
            .AddYabtPackaging()
            .AddYabtSync()
            .AddSingleton(TimeProvider.System)
            .AddSingleton(logFileStartup)
            .AddSingleton<LogFilePathGuard>()
            .AddSingleton<CommandRunner>();

        using var host = builder.Build();
        var runner = host.Services.GetRequiredService<CommandRunner>();
        return await runner.RunAsync(commandArguments);
    }
}

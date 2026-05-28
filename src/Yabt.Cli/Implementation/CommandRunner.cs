using Microsoft.Extensions.Logging;
using Yabt.Sync;

namespace Yabt.Cli.Implementation;

internal sealed class CommandRunner
(
    IArchiveSynchronizer _synchronizer,
    ILogger<CommandRunner> _logger
)
{
    public async Task<int> RunAsync
    (
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default
    )
    {
        if (args.Count == 0 || IsHelp(args[0]))
        {
            WriteUsage();
            return 0;
        }

        var command = args[0];
        if (!YabtCliCommandNames.Known.Contains(command))
        {
            _logger.LogError("Unknown command: {Command}", command);
            WriteUsage();
            return 2;
        }

        return command.ToLowerInvariant() switch
        {
            YabtCliCommandNames.Sync => await RunSyncAsync(args, cancellationToken),
            YabtCliCommandNames.Restore => NotImplemented(command),
            YabtCliCommandNames.Scan => NotImplemented(command),
            YabtCliCommandNames.Verify => NotImplemented(command),
            YabtCliCommandNames.Pack => NotImplemented(command),
            YabtCliCommandNames.Reconcile => NotImplemented(command),
            _ => 2,
        };
    }

    private async Task<int> RunSyncAsync
    (
        IReadOnlyList<string> args,
        CancellationToken cancellationToken
    )
    {
        var sourceRoot = args.Count > 1 ? args[1] : Directory.GetCurrentDirectory();
        var result = await _synchronizer.SyncAsync(
            new(sourceRoot, DryRun: true),
            cancellationToken);

        Console.WriteLine(result.Message);
        return result.Completed ? 0 : 1;
    }

    private static int NotImplemented(string command)
    {
        Console.WriteLine($"Command '{command}' is scaffolded but not implemented yet.");
        return 1;
    }

    private static bool IsHelp(string arg)
    {
        return arg is "-h" or "--help" or "help";
    }

    private static void WriteUsage()
    {
        Console.WriteLine("Usage: yabt <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        foreach (var command in YabtCliCommandNames.Known.Order(StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  {command}");
        }
    }
}

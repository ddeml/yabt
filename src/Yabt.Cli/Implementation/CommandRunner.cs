using System.CommandLine;
using Yabt.Sync;

namespace Yabt.Cli.Implementation;

internal sealed class CommandRunner
(
    IArchiveSynchronizer _synchronizer
)
{
    public async Task<int> RunAsync
    (
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default
    )
    {
        var rootCommand = CreateRootCommand();
        cancellationToken.ThrowIfCancellationRequested();

        if (args.Count == 0 || args[0] is "help")
        {
            return await InvokeAsync(rootCommand, ["--help"], cancellationToken);
        }

        return await InvokeAsync(rootCommand, args, cancellationToken);
    }

    private static async Task<int> InvokeAsync
    (
        RootCommand rootCommand,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken
    )
    {
        return await rootCommand.Parse(args).InvokeAsync(
            new InvocationConfiguration(),
            cancellationToken);
    }

    private RootCommand CreateRootCommand()
    {
        var rootCommand = new RootCommand("Replicate folders to inspectable object-store archives.");

        rootCommand.Subcommands.Add(CreateSyncCommand());
        foreach (var command in YabtCliCommandNames.Known
                     .Where(command => command is not YabtCliCommandNames.Sync)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            rootCommand.Subcommands.Add(CreateScaffoldedCommand(command));
        }

        return rootCommand;
    }

    private Command CreateSyncCommand()
    {
        var sourceRootArgument = new Argument<string>("source-root")
        {
            Description = "Folder to synchronize.",
            DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
        };

        var command = new Command(YabtCliCommandNames.Sync, "Synchronize a folder to the archive.")
        {
            Arguments = { sourceRootArgument },
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var sourceRoot = parseResult.GetValue(sourceRootArgument)
                ?? Directory.GetCurrentDirectory();
            return await RunSyncAsync(sourceRoot, cancellationToken);
        });

        return command;
    }

    private static Command CreateScaffoldedCommand(string commandName)
    {
        var command = new Command(commandName, "Scaffolded command.");
        command.SetAction(_ => NotImplemented(commandName));

        return command;
    }

    private async Task<int> RunSyncAsync(string sourceRoot, CancellationToken cancellationToken)
    {
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
}

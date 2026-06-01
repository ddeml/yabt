using System.Collections.Frozen;
using System.CommandLine;
using Yabt.Sync;

namespace Yabt.Cli.Implementation;

internal sealed class CommandRunner
(
    IArchiveSynchronizer _archiveSynchronizer
)
{
    private static readonly FrozenSet<string> HelpArguments = new[]
    {
        "-h",
        "--help",
        "/?",
        "help",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public async Task<int> RunAsync
    (
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default
    )
    {
        var rootCommand = CreateRootCommand();
        cancellationToken.ThrowIfCancellationRequested();

        if (args.Count == 0 ||
            HelpArguments.Contains(args[0]))
        {
            return await InvokeAsync(rootCommand, ["--help"], cancellationToken);
        }

        return await InvokeAsync(rootCommand, args, cancellationToken);
    }

    private static Task<int> InvokeAsync
    (
        RootCommand rootCommand,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken
    ) => rootCommand.Parse(args).InvokeAsync
    (
        new InvocationConfiguration(),
        cancellationToken
    );

    private RootCommand CreateRootCommand()
    {
        var rootCommand = new RootCommand("Replicate folders to inspectable object-store archives.");

        foreach (var command in YabtCliCommandNames.Known.Order(StringComparer.OrdinalIgnoreCase))
        {
            rootCommand.Subcommands.Add(CreateArchiveCommand(command));
        }

        return rootCommand;
    }

    private Command CreateArchiveCommand(string commandName)
    {
        var sourceRootArgument = new Argument<string>("source-root")
        {
            Description = "Folder to use as the command root.",
            DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Plan the operation without writing changes.",
        };

        var command = new Command(commandName, GetCommandDescription(commandName))
        {
            Arguments = { sourceRootArgument },
            Options = { dryRunOption },
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var sourceRoot = parseResult.GetValue(sourceRootArgument) ?? Directory.GetCurrentDirectory();
            var dryRun = parseResult.GetValue(dryRunOption);
            return await RunArchiveCommandAsync
            (
                commandName,
                sourceRoot,
                dryRun,
                cancellationToken
            );
        });

        return command;
    }

    private async Task<int> RunArchiveCommandAsync
    (
        string commandName,
        string sourceRoot,
        bool dryRun,
        CancellationToken cancellationToken
    )
    {
        var request = new SyncRunRequest(sourceRoot, dryRun);
        var result = commandName switch
        {
            YabtCliCommandNames.Sync => await _archiveSynchronizer.SyncAsync(request, cancellationToken),
            YabtCliCommandNames.Restore => await _archiveSynchronizer.RestoreAsync(request, cancellationToken),
            YabtCliCommandNames.Scan => await _archiveSynchronizer.ScanAsync(request, cancellationToken),
            YabtCliCommandNames.Verify => await _archiveSynchronizer.VerifyAsync(request, cancellationToken),
            YabtCliCommandNames.Pack => await _archiveSynchronizer.PackAsync(request, cancellationToken),
            YabtCliCommandNames.Reconcile => await _archiveSynchronizer.ReconcileAsync(request, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(commandName), commandName, null),
        };

        Console.WriteLine(result.Message);
        return result.Completed ? 0 : 1;
    }

    private static string GetCommandDescription(string commandName)
    {
        return commandName switch
        {
            YabtCliCommandNames.Sync => "Synchronize a folder to the archive.",
            YabtCliCommandNames.Restore => "Restore from an archive.",
            YabtCliCommandNames.Scan => "Scan a folder for future synchronization planning.",
            YabtCliCommandNames.Verify => "Verify a folder against an archive.",
            YabtCliCommandNames.Pack => "Project a folder into a package representation.",
            YabtCliCommandNames.Reconcile => "Reconcile two archive roots.",
            _ => "Run a YABT command.",
        };
    }
}

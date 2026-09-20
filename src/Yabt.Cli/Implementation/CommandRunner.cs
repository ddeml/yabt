using System.Collections.Frozen;
using System.CommandLine;
using Microsoft.Extensions.Logging;
using Yabt.Common;
using Yabt.Sync;

namespace Yabt.Cli.Implementation;

internal sealed class CommandRunner
(
    IArchiveSynchronizer _archiveSynchronizer,
    IRestorePathVerifier _restorePathVerifier,
    IHistoryDeduplicator _historyDeduplicator,
    ILogger<CommandRunner> _logger,
    LogFileStartup _logFileStartup,
    LogFilePathGuard _logFilePathGuard
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
        _logger.LogTrace(nameof(RunAsync));

        var rootCommand = CreateRootCommand();
        cancellationToken.ThrowIfCancellationRequested();

        if (args.Count == 0 ||
            HelpArguments.Contains(args[0]))
        {
            return await InvokeAsync(rootCommand, ["--help"], cancellationToken);
        }

        try
        {
            return await InvokeAsync(rootCommand, args, cancellationToken);
        }
        catch (YabtException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Command failed: {ErrorMessage}", ex.Message);
            }
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static Task<int> InvokeAsync
    (
        RootCommand rootCommand,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken
    ) => rootCommand.Parse(args).InvokeAsync
    (
        new InvocationConfiguration
        {
            EnableDefaultExceptionHandler = false,
        },
        cancellationToken
    );

    private RootCommand CreateRootCommand()
    {
        _logger.LogTrace(nameof(CreateRootCommand));

        var rootCommand = new RootCommand("Replicate folders to inspectable object-store archives.")
        {
            Options = { LogFileCommandLine.CreateOption() },
        };

        foreach (var command in YabtCliCommandNames.Known.Order(StringComparer.OrdinalIgnoreCase))
        {
            var archiveCommand = command switch
            {
                YabtCliCommandNames.Deduplicate => CreateDeduplicateCommand(),
                YabtCliCommandNames.VerifyRestore => CreateVerifyRestoreCommand(),
                _ => CreateArchiveCommand(command),
            };
            rootCommand.Subcommands.Add(archiveCommand);
        }

        return rootCommand;
    }

    private Command CreateArchiveCommand(string commandName)
    {
        _logger.LogTrace(nameof(CreateArchiveCommand));

        var sourceRootArgument = new Argument<string>("source-root")
        {
            Description = "Folder to use as the command root.",
            DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Plan the operation without writing changes.",
        };
        var targetStoreIdOption = new Option<string?>("--target-store-id")
        {
            Description = "Archive store id from the root descriptor.",
        };
        var byteForByteOption = new Option<bool>("--byte-for-byte")
        {
            Description = "Compare full file contents instead of using metadata fingerprints.",
        };
        var destinationRootOption = new Option<string?>("--destination-root")
        {
            Description = "Filesystem folder to reconcile to the restored live state; " +
                "relative paths use the current working directory.",
            Required = true,
        };
        var replaceRootDescriptorOption = new Option<bool>("--replace-root-descriptor")
        {
            Description = "Historize and replace a different destination .yabt-root.json " +
                "with the exact descriptor stored in the archive.",
        };

        var command = new Command(commandName, GetCommandDescription(commandName))
        {
            Arguments = { sourceRootArgument },
            Options = { dryRunOption, targetStoreIdOption },
        };
        if (commandName == YabtCliCommandNames.Backup)
        {
            command.Aliases.Add(YabtCliCommandNames.SyncAlias);
        }

        var supportsByteForByte = commandName is
            YabtCliCommandNames.Backup or
            YabtCliCommandNames.Restore or
            YabtCliCommandNames.Verify;
        if (supportsByteForByte)
        {
            command.Options.Add(byteForByteOption);
        }
        if (commandName == YabtCliCommandNames.Restore)
        {
            command.Options.Add(destinationRootOption);
            command.Options.Add(replaceRootDescriptorOption);
        }

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var sourceRoot = parseResult.GetValue(sourceRootArgument) ?? Directory.GetCurrentDirectory();
            var dryRun = parseResult.GetValue(dryRunOption);
            var targetStoreId = parseResult.GetValue(targetStoreIdOption);
            var byteForByte = supportsByteForByte && parseResult.GetValue(byteForByteOption);
            var destinationRoot = commandName == YabtCliCommandNames.Restore ?
                parseResult.GetValue(destinationRootOption) :
                null;
            var replaceRootDescriptor = commandName == YabtCliCommandNames.Restore &&
                parseResult.GetValue(replaceRootDescriptorOption);
            await EnsureFileLoggingStartedAsync(
                commandName,
                sourceRoot,
                targetStoreId,
                destinationRoot,
                cancellationToken);
            return await RunArchiveCommandAsync
            (
                commandName,
                sourceRoot,
                dryRun,
                targetStoreId,
                byteForByte,
                destinationRoot,
                replaceRootDescriptor,
                cancellationToken
            );
        });

        return command;
    }

    private Command CreateDeduplicateCommand()
    {
        _logger.LogTrace(nameof(CreateDeduplicateCommand));

        var archiveRootArgument = new Argument<string>("archive-root")
        {
            Description = "Archive root whose history should be deduplicated.",
            DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Report deduplication changes without writing them.",
        };
        var targetStoreIdOption = new Option<string?>("--target-store-id")
        {
            Description = "Target store id from the root descriptor.",
        };

        var command = new Command
        (
            YabtCliCommandNames.Deduplicate,
            GetCommandDescription(YabtCliCommandNames.Deduplicate)
        )
        {
            Arguments = { archiveRootArgument },
            Options = { dryRunOption, targetStoreIdOption },
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var archiveRoot = parseResult.GetValue(archiveRootArgument) ?? Directory.GetCurrentDirectory();
            var dryRun = parseResult.GetValue(dryRunOption);
            var targetStoreId = parseResult.GetValue(targetStoreIdOption);
            await EnsureFileLoggingStartedAsync(
                YabtCliCommandNames.Deduplicate,
                archiveRoot,
                targetStoreId,
                destinationRoot: null,
                cancellationToken);
            var request = new HistoryDeduplicationRequest(archiveRoot, dryRun, targetStoreId);
            var result = await _historyDeduplicator.DeduplicateAsync(request, cancellationToken);

            Console.WriteLine(result.Message);
            return result.Completed ? 0 : 1;
        });

        return command;
    }

    private Command CreateVerifyRestoreCommand()
    {
        _logger.LogTrace(nameof(CreateVerifyRestoreCommand));

        var sourceRootArgument = new Argument<string>("source-root")
        {
            Description = "Original filesystem folder to compare.",
            DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
        };
        var destinationRootOption = new Option<string?>("--destination-root")
        {
            Description = "Restored filesystem folder to compare byte-for-byte; " +
                "relative paths use the current working directory.",
            Required = true,
        };
        var command = new Command
        (
            YabtCliCommandNames.VerifyRestore,
            GetCommandDescription(YabtCliCommandNames.VerifyRestore)
        )
        {
            Arguments = { sourceRootArgument },
            Options = { destinationRootOption },
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var sourceRoot = parseResult.GetValue(sourceRootArgument) ??
                Directory.GetCurrentDirectory();
            var destinationRoot = parseResult.GetValue(destinationRootOption);
            await EnsureFileLoggingStartedAsync(
                YabtCliCommandNames.VerifyRestore,
                sourceRoot,
                targetStoreId: null,
                destinationRoot,
                cancellationToken);
            var request = new RestorePathVerificationRequest(
                sourceRoot,
                destinationRoot ?? string.Empty);
            var result = await _restorePathVerifier.VerifyAsync(request, cancellationToken);

            Console.WriteLine(result.Message);
            return result.Identical ? 0 : 1;
        });

        return command;
    }

    private async Task EnsureFileLoggingStartedAsync
    (
        string commandName,
        string commandRoot,
        string? targetStoreId,
        string? destinationRoot,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(EnsureFileLoggingStartedAsync));

        if (!_logFileStartup.IsEnabled)
        {
            return;
        }

        await _logFilePathGuard.EnsureOutsideOperationRootsAsync(
            _logFileStartup.Path,
            commandName,
            commandRoot,
            targetStoreId,
            destinationRoot,
            cancellationToken);
        _logFileStartup.Start();
        Console.WriteLine($"Log file: {_logFileStartup.Path}");
    }

    private async Task<int> RunArchiveCommandAsync
    (
        string commandName,
        string sourceRoot,
        bool dryRun,
        string? targetStoreId,
        bool byteForByte,
        string? destinationRoot,
        bool replaceRootDescriptor,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(RunArchiveCommandAsync));

        var request = new SyncRunRequest
        (
            sourceRoot,
            dryRun,
            targetStoreId,
            byteForByte,
            destinationRoot,
            replaceRootDescriptor
        );
        var result = commandName switch
        {
            YabtCliCommandNames.Backup => await _archiveSynchronizer.BackupAsync(request, cancellationToken),
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
            YabtCliCommandNames.Backup => "Back up a folder to the archive.",
            YabtCliCommandNames.Restore => "Restore from an archive.",
            YabtCliCommandNames.VerifyRestore =>
                "Compare a restored filesystem folder with its original source byte-for-byte.",
            YabtCliCommandNames.Scan => "Scan a folder for future synchronization planning.",
            YabtCliCommandNames.Verify =>
                "Quickly verify a folder from metadata fingerprints; use --byte-for-byte for full comparison.",
            YabtCliCommandNames.Pack => "Project a folder into a package representation.",
            YabtCliCommandNames.Reconcile => "Reconcile two archive roots.",
            YabtCliCommandNames.Deduplicate =>
                "Deduplicate history after mandatory byte-for-byte confirmation.",
            _ => "Run a YABT command.",
        };
    }
}

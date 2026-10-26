using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Yabt.Common;
using Yabt.Profiling.Implementation;
using Yabt.Sync;

namespace Yabt.Profiling;

internal static class Program
{
    private const int RetainedCheckpointDelayMilliseconds = 100;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                Console.WriteLine("prepare <fixture> <file-count> <flat|grouped> <mirror|zip>");
                Console.WriteLine("destination <fixture> <empty|unchanged|mixed> <new-name>");
                Console.WriteLine
                (
                    "resume <new-fixture> <file-count> <mirror|child-zip|root-zip> " +
                        "<backup|restore> <progress-on|progress-off> <abort-after-commits|0> <new-report.json>"
                );
                Console.WriteLine
                (
                    "run <fixture> <destination-name> <plan|restore|backup> " +
                        "<normal|retained> <new-report.json> [warmup-fixture] [pause-phase]"
                );
                return 0;
            }

            var maximumSeconds = ReadPositiveSetting("YABT_PROFILE_MAX_SECONDS", 600);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(maximumSeconds));
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            switch (args[0])
            {
                case "prepare" when args.Length == 5:
                {
                    var fixture = await ProfilingFixtures.CreateSourceAsync
                    (
                        args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
                        args[3], args[4], cancellation.Token
                    );
                    await using var services = ProfilingServices.Create(fixture.ScratchRoot);
                    await ProfilingFixtures.PrepareArchiveAsync
                    (
                        fixture, services.GetRequiredService<IArchiveSynchronizer>(), cancellation.Token
                    );
                    Console.WriteLine(fixture.RootPath);
                    return 0;
                }
                case "destination" when args.Length == 4:
                {
                    var fixture = await ProfilingFixtures.LoadAsync(args[1], cancellation.Token);
                    await using var services = ProfilingServices.Create(fixture.ScratchRoot);
                    var path = await ProfilingFixtures.PrepareDestinationAsync
                    (
                        fixture, args[2], args[3],
                        services.GetRequiredService<IArchiveSynchronizer>(), cancellation.Token
                    );
                    Console.WriteLine(path);
                    return 0;
                }
                case "run" when args.Length is >= 6 and <= 8:
                    return await RunAsync(args, maximumSeconds, cancellation);
                case "resume" when args.Length == 8:
                    return await ResumeProfiling.RunAsync
                    (
                        args, maximumSeconds, ReadPositiveSetting("YABT_PROFILE_MAX_PRIVATE_MIB", 2048) * 1024L * 1024,
                        cancellation.Token
                    );
                case "resume-worker" when args.Length == 6:
                    return await ResumeProfiling.RunWorkerAsync(args, cancellation.Token);
                default:
                    throw new ArgumentException("Invalid profiling arguments. Run without arguments for usage.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task<int> RunAsync
    (
        string[] args,
        int maximumSeconds,
        CancellationTokenSource cancellation
    )
    {
        var fixture = await ProfilingFixtures.LoadAsync(args[1], cancellation.Token);
        var destinationName = args[2];
        if
        (
            destinationName.Length == 0 || destinationName is "." or ".." ||
            destinationName.Contains('/') || destinationName.Contains('\\') ||
            destinationName.Contains(':')
        )
        {
            throw new ArgumentException("Select one prepared destination by its immediate child name.");
        }

        var destinationRoot = Path.Combine(fixture.DestinationsRoot, destinationName);
        if (!Directory.Exists(destinationRoot))
        {
            throw new IOException("Prepare the destination before starting the measured process.");
        }

        var operation = args[3];
        if (operation is not ("plan" or "restore" or "backup"))
        {
            throw new ArgumentException("Operation must be plan, restore, or backup.");
        }

        var mode = args[4];
        if (mode is not ("normal" or "retained"))
        {
            throw new ArgumentException("Mode must be normal or retained.");
        }

        if (mode == "retained" && operation != "plan")
        {
            throw new ArgumentException("Retained phase diagnostics apply only to read-only planning.");
        }

        var reportPath = Path.GetFullPath(args[5]);
        RequireExternalNewReport(reportPath, fixture.RootPath);
        var pausePhase = args.Length == 8 ? args[7] : null;
        if (pausePhase is not null && (mode != "retained" || operation != "plan"))
        {
            throw new ArgumentException("Heap-dump pauses require retained planning mode.");
        }

        if (args.Length >= 7)
        {
            var warmup = await ProfilingFixtures.LoadAsync(args[6], cancellation.Token);
            if (warmup.FileCount > 10) { throw new ArgumentException("Warmup fixture must have at most ten files."); }
            await WarmupAsync(warmup, cancellation.Token);
        }

        // Give producer completion stacks time to unwind before retention probes.
        // Whole-command timing keeps its existing warmup and baseline behavior.
        if (mode == "retained")
        {
            await Task.Delay(RetainedCheckpointDelayMilliseconds, cancellation.Token);
        }

        // Warmup uses separate owners and returns before collecting its garbage.
        ProfilingSampler.Collect();
        await using var services = ProfilingServices.Create(fixture.ScratchRoot);
        var maximumPrivateBytes = ReadPositiveSetting("YABT_PROFILE_MAX_PRIVATE_MIB", 2048) * 1024L * 1024;
        var sampler = new ProfilingSampler(fixture.ScratchRoot, mode == "retained", maximumPrivateBytes, cancellation);
        object? result = null;
        string? failure = null;
        var startedUtc = DateTimeOffset.UtcNow;
        sampler.Capture("baseline");
        var stopwatch = Stopwatch.StartNew();
        var paused = false;
        try
        {
            if (operation == "plan")
            {
                result = await new ProfilingOperations(services).MeasureRestorePlanningAsync
                (
                    new(fixture.SourceRoot, DryRun: true, DestinationRoot: destinationRoot),
                    async (phase, counts) =>
                    {
                        // Async producer completion can invoke this callback inline.
                        // Let those stacks unwind before measuring owned retention.
                        if (mode == "retained" || phase == "released")
                        {
                            await Task.Delay(RetainedCheckpointDelayMilliseconds, cancellation.Token);
                        }
                        sampler.Capture(phase, counts);
                        if (phase != pausePhase) { return; }
                        paused = true;
                        await PauseForHeapDumpAsync(reportPath, phase, cancellation.Token);
                    },
                    cancellation.Token
                );
            }
            else
            {
                var synchronizer = services.GetRequiredService<IArchiveSynchronizer>();
                var runResult = operation == "restore" ?
                    await synchronizer.RestoreAsync
                    (
                        new(fixture.SourceRoot, DestinationRoot: destinationRoot), cancellation.Token
                    ) :
                    await synchronizer.BackupAsync(new(fixture.SourceRoot), cancellation.Token);
                if (!runResult.Completed) { throw new InvalidOperationException(runResult.Message); }
                result = runResult;
            }

            if (pausePhase is not null && !paused)
            {
                throw new ArgumentException($"The requested pause phase '{pausePhase}' was not reached.");
            }
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
        }
        finally
        {
            stopwatch.Stop();
            try
            {
                sampler.Capture("finished");
            }
            finally
            {
                await sampler.DisposeAsync();
            }
        }

        var temporaryOptions = services.GetRequiredService<IOptionsMonitor<TemporaryContentOptions>>().CurrentValue;
        var syncOptions = services.GetRequiredService<IOptionsMonitor<YabtSyncOptions>>().CurrentValue;
        var report = new
        {
            schemaVersion = 1,
            startedUtc,
            fixture,
            destinationName,
            operation,
            mode,
            retainedCheckpointDelayMilliseconds = mode == "retained" ?
                RetainedCheckpointDelayMilliseconds : 0,
            warmupFixture = args.Length >= 7 ? Path.GetFullPath(args[6]) : null,
            machine = Environment.MachineName,
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processors = Environment.ProcessorCount,
            serverGc = GCSettings.IsServerGC,
            build = typeof(IArchiveSynchronizer).Assembly.
                GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            restoreMaximumConcurrency = syncOptions.GetValidatedRestoreMaximumConcurrency(),
            payloadMemoryThresholdBytes = temporaryOptions.GetEffectiveMemoryThresholdBytes(),
            payloadTotalMemoryBudgetBytes = temporaryOptions.GetEffectiveTotalMemoryBudgetBytes(),
            maximumSeconds,
            maximumPrivateBytes,
            sampleIntervalMilliseconds = ProfilingSampler.IntervalMilliseconds,
            elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            sampler.SampleCount,
            sampler.SampledPeakPrivateBytes,
            sampler.SampledPeakWorkingSetBytes,
            sampler.SampledPeakManagedBytes,
            sampler.Checkpoints,
            result,
            failure = failure ?? sampler.LimitFailure,
            timingComparable = mode == "normal" && operation != "plan" && failure is null && sampler.LimitFailure is null,
        };
        await using (var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            await JsonSerializer.SerializeAsync(output, report, JsonOptions);
        }

        Console.WriteLine(reportPath);
        return report.failure is null ? 0 : 1;
    }

    private static async Task WarmupAsync(ProfilingFixture fixture, CancellationToken cancellationToken)
    {
        await using var services = ProfilingServices.Create(fixture.ScratchRoot);
        var destination = Path.Combine(fixture.DestinationsRoot, "unchanged");
        await new ProfilingOperations(services).MeasureRestorePlanningAsync
        (
            new(fixture.SourceRoot, DryRun: true, DestinationRoot: destination),
            static (_, _) => Task.CompletedTask,
            cancellationToken
        );
    }

    private static async Task PauseForHeapDumpAsync
    (
        string reportPath,
        string phase,
        CancellationToken cancellationToken
    )
    {
        var checkpointPath = reportPath + ".checkpoint.json";
        var resumePath = reportPath + ".resume";
        await using (var output = new FileStream(checkpointPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            await JsonSerializer.SerializeAsync
            (
                output,
                new { processId = Environment.ProcessId, phase, resumePath }, JsonOptions, cancellationToken
            );
        }

        Console.WriteLine($"Paused at {phase}; PID {Environment.ProcessId}; create {resumePath} to resume.");
        while (!File.Exists(resumePath)) { await Task.Delay(250, cancellationToken); }
    }

    private static int ReadPositiveSetting(string name, int fallback)
    {
        var setting = Environment.GetEnvironmentVariable(name);
        var value = setting is null ? fallback :
            int.Parse(setting, System.Globalization.CultureInfo.InvariantCulture);
        return value > 0 ? value : throw new ArgumentOutOfRangeException(name);
    }

    internal static void RequireExternalNewReport(string reportPath, string fixtureRoot)
    {
        var relative = Path.GetRelativePath(fixtureRoot, reportPath);
        if
        (
            relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative)
        )
        {
            throw new ArgumentException("Reports must be outside the measured fixture tree.");
        }

        if (!Directory.Exists(Path.GetDirectoryName(reportPath)))
        {
            throw new DirectoryNotFoundException("Create the report parent directory before measuring.");
        }

        if (File.Exists(reportPath) || File.Exists(reportPath + ".checkpoint.json") || File.Exists(reportPath + ".resume"))
        {
            throw new IOException("Choose a fresh report path; existing results are never overwritten.");
        }
    }
}

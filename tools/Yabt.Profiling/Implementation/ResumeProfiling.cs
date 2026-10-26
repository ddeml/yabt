using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Yabt.Common;
using Yabt.Core.Models;
using Yabt.Metadata;
using Yabt.Sync;

namespace Yabt.Profiling.Implementation;

internal static class ResumeProfiling
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const int MaximumFileCount = 100000;

    internal static async Task<int> RunAsync
    (
        string[] args,
        int maximumSeconds,
        long maximumPrivateBytes,
        CancellationToken cancellationToken
    )
    {
        var fileCount = int.Parse(args[2], CultureInfo.InvariantCulture);
        var format = args[3];
        var operation = args[4];
        var enabled = args[5] switch
        {
            "progress-on" => true,
            "progress-off" => false,
            _ => throw new ArgumentException("Choose progress-on or progress-off."),
        };
        var abortAfter = int.Parse(args[6], CultureInfo.InvariantCulture);
        ValidateSettings(fileCount, format, operation, abortAfter, maximumSeconds, maximumPrivateBytes);
        var reportPath = Path.GetFullPath(args[7]);
        Program.RequireExternalNewReport(reportPath, Path.GetFullPath(args[1]));
        string[] sidecars =
        [
            ".scenario.json", ".first.json", ".first.commits.jsonl", ".resume.json", ".resume.commits.jsonl"
        ];
        foreach (var suffix in sidecars)
        {
            if (File.Exists(reportPath + suffix)) { throw new IOException("Choose fresh resume report sidecar paths."); }
        }

        var fixture = await ProfilingFixtures.CreateSourceAsync
        (
            args[1], fileCount, "grouped", format == "root-zip" ? "zip" : "mirror", cancellationToken
        );
        if (format == "child-zip")
        {
            foreach (var child in Directory.EnumerateDirectories(fixture.SourceRoot))
            {
                var policyPath = Path.Combine(child, FolderPolicyFileNames.Primary);
                await WriteNewJsonAsync(policyPath, new FolderPolicy("zip"), cancellationToken);
                File.SetLastWriteTimeUtc(policyPath, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            }
        }
        var scenario = new ResumeProfilingScenario
        (
            fixture, format, operation, enabled, abortAfter, maximumSeconds, maximumPrivateBytes
        );
        Directory.CreateDirectory(scenario.ProgressDirectory);
        Directory.CreateDirectory(scenario.DestinationRoot);
        if (operation == "restore")
        {
            await using var setup = ProfilingServices.Create
            (
                fixture.ScratchRoot, scenario.ProgressDirectory, progressEnabled: false
            );
            await ProfilingFixtures.PrepareArchiveAsync
            (
                fixture, setup.GetRequiredService<IArchiveSynchronizer>(), cancellationToken
            );
        }
        var scenarioPath = reportPath + ".scenario.json";
        await WriteNewJsonAsync(scenarioPath, scenario, cancellationToken);
        var firstReportPath = reportPath + ".first.json";
        var firstCommitsPath = reportPath + ".first.commits.jsonl";
        await RunWorkerAsync(scenarioPath, "first", firstReportPath, firstCommitsPath, "-", cancellationToken);
        JsonElement? resumed = null;
        if (abortAfter > 0)
        {
            var resumeReportPath = reportPath + ".resume.json";
            await RunWorkerAsync
            (
                scenarioPath, "resume", resumeReportPath, reportPath + ".resume.commits.jsonl", firstCommitsPath,
                cancellationToken
            );
            resumed = await ReadJsonAsync<JsonElement>(resumeReportPath, cancellationToken);
        }
        var report = new
        {
            schemaVersion = 1,
            scenario,
            observer = "Structured commit and provider invocation events; no byte counters. Fresh worker process per attempt.",
            first = await ReadJsonAsync<JsonElement>(firstReportPath, cancellationToken),
            resumed,
        };
        await WriteNewJsonAsync(reportPath, report, cancellationToken);
        Console.WriteLine(reportPath);
        return 0;
    }

    internal static async Task<int> RunWorkerAsync(string[] args, CancellationToken cancellationToken)
    {
        var scenario = await ReadJsonAsync<ResumeProfilingScenario>(args[1], cancellationToken);
        if (scenario.Fixture is null)
        {
            throw new InvalidDataException("A resume scenario must identify its owned profiling fixture.");
        }
        ValidateSettings
        (
            scenario.Fixture.FileCount, scenario.Format, scenario.Operation, scenario.AbortAfterPayloadCommits,
            scenario.MaximumSeconds, scenario.MaximumPrivateBytes
        );
        if
        (
            scenario.Fixture.Shape != "grouped" ||
            scenario.Fixture.Format != (scenario.Format == "root-zip" ? "zip" : "mirror")
        )
        {
            throw new InvalidDataException("The fixture shape or format does not match the resume scenario.");
        }
        if
        (
            string.IsNullOrWhiteSpace(scenario.Fixture.RootPath) ||
            !Path.IsPathFullyQualified(scenario.Fixture.RootPath)
        )
        {
            throw new InvalidDataException("Resume fixture roots must be fully qualified native paths.");
        }
        var phase = args[2];
        if (phase is not ("first" or "resume")) { throw new ArgumentException("Unknown resume worker phase."); }
        if
        (
            phase == "first" && args[5] != "-" ||
            phase == "resume" && (args[5] == "-" || scenario.AbortAfterPayloadCommits == 0)
        )
        {
            throw new ArgumentException("The worker phase and prior-commit log do not match the interruption scenario.");
        }
        await ProfilingFixtures.ValidateResumeFixtureAsync(scenario, cancellationToken);
        var reportPath = Path.GetFullPath(args[3]);
        Program.RequireExternalNewReport(reportPath, scenario.Fixture.RootPath);
        var commitPath = Path.GetFullPath(args[4]);
        Program.RequireExternalNewReport(commitPath, scenario.Fixture.RootPath);
        var abortAfter = phase == "first" ? scenario.AbortAfterPayloadCommits : 0;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var observer = new ResumeProfilingObserver
        (
            scenario, commitPath, args[5] == "-" ? null : args[5], abortAfter, operationCancellation
        );
        await using var services = ProfilingServices.Create
        (
            scenario.Fixture.ScratchRoot, scenario.ProgressDirectory, scenario.ProgressEnabled, observer
        );
        ProfilingSampler.Collect();
        var sampler = new ProfilingSampler
        (
            scenario.Fixture.ScratchRoot, retained: false, scenario.MaximumPrivateBytes, operationCancellation
        );
        sampler.Capture("baseline");
        var startedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        observer.Start();
        SyncRunResult? result = null;
        string? failure = null;
        var interrupted = false;
        try
        {
            var synchronizer = services.GetRequiredService<IArchiveSynchronizer>();
            result = scenario.Operation == "backup" ?
                await synchronizer.BackupAsync(new(scenario.Fixture.SourceRoot), operationCancellation.Token) :
                await synchronizer.RestoreAsync
                (
                    new(scenario.Fixture.SourceRoot, DestinationRoot: scenario.DestinationRoot), operationCancellation.Token
                );
            if (!result.Completed) { failure = result.Message; }
        }
        catch (Exception ex)
        {
            interrupted = observer.RequestedInterruption && ex.IsCancellationException() &&
                !cancellationToken.IsCancellationRequested;
            if (!interrupted) { failure = ex.ToString(); }
        }
        finally
        {
            stopwatch.Stop();
            observer.Dispose();
            try { sampler.Capture("finished"); }
            finally { await sampler.DisposeAsync(); }
        }
        if (abortAfter > 0 && !interrupted)
        {
            failure ??= "The requested deterministic interruption did not happen; choose an attainable commit count.";
        }
        if (observer.Snapshot().ProgressUnavailableWarnings != 0)
        {
            failure ??= "Progress storage was unavailable; this run cannot measure the selected progress setting.";
        }
        failure ??= sampler.LimitFailure;
        var checkpoints = sampler.Checkpoints;
        var syncOptions = services.GetRequiredService<IOptionsMonitor<YabtSyncOptions>>().CurrentValue;
        var temporaryOptions = services.GetRequiredService<IOptionsMonitor<TemporaryContentOptions>>().CurrentValue;
        var report = new
        {
            schemaVersion = 1,
            startedUtc,
            phase,
            operation = scenario.Operation,
            scenario.Format,
            scenario.ProgressEnabled,
            abortAfter,
            processId = Environment.ProcessId,
            machine = Environment.MachineName,
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processors = Environment.ProcessorCount,
            serverGc = GCSettings.IsServerGC,
            build = typeof(IArchiveSynchronizer).Assembly.
                GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            warmup = false,
            baselineCollection = true,
            sampleIntervalMilliseconds = ProfilingSampler.IntervalMilliseconds,
            restoreMaximumConcurrency = syncOptions.GetValidatedRestoreMaximumConcurrency(),
            progressFlushUnitCount = syncOptions.GetValidatedProgressFlushUnitCount(),
            progressFlushIntervalSeconds = syncOptions.GetValidatedProgressFlushInterval().TotalSeconds,
            payloadMemoryThresholdBytes = temporaryOptions.GetEffectiveMemoryThresholdBytes(),
            payloadTotalMemoryBudgetBytes = temporaryOptions.GetEffectiveTotalMemoryBudgetBytes(),
            completed = result?.Completed ?? false,
            interrupted,
            elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            observer.FirstPayloadCommitMilliseconds,
            observer.FirstNewPayloadCommitMilliseconds,
            observer.PayloadCommitCount,
            observer.PreviouslyCommittedCount,
            observer.RecommittedPayloadCount,
            observer.CallsBeforeFirstNewCommit,
            calls = observer.Snapshot(),
            allocatedBytes = checkpoints[^1].TotalAllocatedBytes - checkpoints[0].TotalAllocatedBytes,
            processCpuMilliseconds = checkpoints[^1].ProcessCpuMilliseconds - checkpoints[0].ProcessCpuMilliseconds,
            sampler.SampledPeakManagedBytes,
            sampler.SampledPeakPrivateBytes,
            sampler.SampledPeakWorkingSetBytes,
            sampler.SampleCount,
            checkpoints,
            remainingProgressDatabases = Directory.EnumerateFiles(scenario.ProgressDirectory, "*.sqlite").Count(),
            result,
            failure,
            timingComparable = failure is null,
        };
        await WriteNewJsonAsync(reportPath, report, default);
        return failure is null ? 0 : 1;
    }

    private static void ValidateSettings
    (
        int fileCount,
        string format,
        string operation,
        int abortAfter,
        int maximumSeconds,
        long maximumPrivateBytes
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fileCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fileCount, MaximumFileCount);
        if (format is not ("mirror" or "child-zip" or "root-zip"))
        {
            throw new ArgumentException("Resume format must be mirror, child-zip, or root-zip.");
        }
        if (operation is not ("backup" or "restore"))
        {
            throw new ArgumentException("Resume operation must be backup or restore.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(abortAfter);
        var payloadCommitCount = operation == "backup" ? format switch
        {
            "root-zip" => 1,
            "child-zip" => (fileCount + ProfilingFixtures.GroupSize - 1) / ProfilingFixtures.GroupSize,
            _ => fileCount,
        } : fileCount;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(abortAfter, payloadCommitCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPrivateBytes);
    }

    private static async Task RunWorkerAsync
    (
        string scenarioPath,
        string phase,
        string reportPath,
        string commitPath,
        string priorCommits,
        CancellationToken cancellationToken
    )
    {
        var hostPath = Environment.ProcessPath ??
            throw new InvalidOperationException("The profiling host executable is unavailable.");
        var start = new ProcessStartInfo(hostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(hostPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(ResumeProfiling).Assembly.Location);
        }
        start.ArgumentList.Add("resume-worker");
        start.ArgumentList.Add(scenarioPath);
        start.ArgumentList.Add(phase);
        start.ArgumentList.Add(reportPath);
        start.ArgumentList.Add(commitPath);
        start.ArgumentList.Add(priorCommits);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The profiling worker did not start.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException
            (
                $"The {phase} profiling worker failed. Inspect '{reportPath}'. {await output} {await error}"
            );
        }
        await Task.WhenAll(output, error);
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken) ??
            throw new InvalidDataException($"Profiling document '{path}' is empty.");
    }

    private static async Task WriteNewJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
    }
}

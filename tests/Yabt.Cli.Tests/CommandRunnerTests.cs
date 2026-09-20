using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Cli.Implementation;
using Yabt.Metadata;
using Yabt.Sync;

namespace Yabt.Cli.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CommandRunnerTests
{
    [TestMethod]
    [DataRow(true, 0)]
    [DataRow(false, 1)]
    public async Task VerifyRestoreDispatchesPathsAndMapsResultToExitCode
    (
        bool identical,
        int expectedExitCode
    )
    {
        const string resultMessage = "verification result";
        var verifier = new CapturingRestorePathVerifier(new(
            identical,
            resultMessage));
        using var logFileStartup = LogFileStartup.Prepare(new(false, null));
        var runner = new CommandRunner(
            new ThrowingArchiveSynchronizer(),
            verifier,
            new ThrowingHistoryDeduplicator(),
            NullLogger<CommandRunner>.Instance,
            logFileStartup,
            new LogFilePathGuard(new ThrowingBackupRootLocator()));
        using var output = new StringWriter();
        var originalOutput = Console.Out;

        try
        {
            Console.SetOut(output);
            var exitCode = await runner.RunAsync
            ([
                YabtCliCommandNames.VerifyRestore,
                "original",
                "--destination-root",
                "restored",
            ]);

            Assert.AreEqual(expectedExitCode, exitCode);
            Assert.IsNotNull(verifier.Request);
            Assert.AreEqual("original", verifier.Request.SourceRoot);
            Assert.AreEqual("restored", verifier.Request.RestoreRoot);
            StringAssert.Contains(output.ToString(), resultMessage);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    [TestMethod]
    public async Task VerifyRestoreRequiresDestinationRoot()
    {
        var verifier = new CapturingRestorePathVerifier(new(
            Identical: true,
            Message: "must not be returned"));
        using var logFileStartup = LogFileStartup.Prepare(new(false, null));
        var runner = new CommandRunner(
            new ThrowingArchiveSynchronizer(),
            verifier,
            new ThrowingHistoryDeduplicator(),
            NullLogger<CommandRunner>.Instance,
            logFileStartup,
            new LogFilePathGuard(new ThrowingBackupRootLocator()));

        var exitCode = await runner.RunAsync
        ([
            YabtCliCommandNames.VerifyRestore,
            "original",
        ]);

        Assert.AreNotEqual(0, exitCode);
        Assert.IsNull(verifier.Request);
    }

    private sealed class CapturingRestorePathVerifier
    (
        RestorePathVerificationResult _result
    ) : IRestorePathVerifier
    {
        public RestorePathVerificationRequest? Request { get; private set; }

        public Task<RestorePathVerificationResult> VerifyAsync
        (
            RestorePathVerificationRequest request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingArchiveSynchronizer : IArchiveSynchronizer
    {
        public Task<SyncRunResult> BackupAsync
        (
            SyncRunRequest request,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException();

        public Task<SyncRunResult> SyncAsync
        (
            SyncRunRequest request,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException();

        public Task<SyncRunResult> RestoreAsync
        (
            SyncRunRequest request,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException();

        public Task<SyncRunResult> ScanAsync
        (
            SyncRunRequest request,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException();

        public Task<SyncRunResult> VerifyAsync
        (
            SyncRunRequest request,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException();

        public Task<SyncRunResult> PackAsync
        (
            SyncRunRequest request,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException();

        public Task<SyncRunResult> ReconcileAsync
        (
            SyncRunRequest request,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException();
    }

    private sealed class ThrowingHistoryDeduplicator : IHistoryDeduplicator
    {
        public Task<HistoryDeduplicationResult> DeduplicateAsync
        (
            HistoryDeduplicationRequest request,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException();
    }

    private sealed class ThrowingBackupRootLocator : IBackupRootLocator
    {
        public Task<BackupRootLocation> LocateRootAsync
        (
            string startPath,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException();
    }
}

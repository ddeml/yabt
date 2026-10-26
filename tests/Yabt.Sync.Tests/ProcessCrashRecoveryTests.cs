using System.Diagnostics;
using System.Text.Json;
using Yabt.Sync.Tests.Implementation;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class ProcessCrashRecoveryTests
{
    [TestMethod]
    [DataRow("backup-history")]
    [DataRow("backup-commit")]
    [DataRow("backup-package")]
    [DataRow("backup-finalization")]
    [DataRow("restore-history")]
    [DataRow("restore-commit")]
    [DataRow("restore-finalization")]
    public async Task FreshProcessRecoversAfterForcedTermination(string scenario)
    {
        var workerRoot = Environment.GetEnvironmentVariable(CrashRecoveryProcess.RootVariable);
        if (workerRoot is not null)
        {
            // vstest selects the method, including every data row. Only the
            // explicitly selected row runs in the child; none launch children.
            if (Environment.GetEnvironmentVariable(CrashRecoveryProcess.ScenarioVariable) != scenario) { return; }
            await RunWorkerAsync(workerRoot, scenario);
            return;
        }

        using var fixture = await CrashRecoveryFixture.CreateAsync();
        var operationToken = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(fixture.RootPath, "worker-token"), operationToken);
        var backup = scenario.StartsWith("backup-", StringComparison.Ordinal);
        if (backup) { await BackupCrashRecoveryScenario.PrepareAsync(fixture, scenario); }
        else { await RestoreCrashRecoveryScenario.PrepareAsync(fixture, scenario); }

        var crashedProcess = await CrashRecoveryProcess.KillAtGateAsync(fixture, scenario, operationToken);

        Assert.IsFalse(File.Exists(Path.Combine(fixture.RootPath, "crash-finally")), "The killed worker must not unwind.");
        Assert.HasCount(1, Directory.GetFiles(fixture.ProgressDirectory, "*.sqlite"), "The interrupted journal must survive.");
        if (backup)
        {
            BackupCrashRecoveryScenario.AssertInterrupted(fixture, scenario);
            await BackupCrashRecoveryScenario.ChangeDuringDowntimeAsync(fixture, scenario);
        }
        else { RestoreCrashRecoveryScenario.AssertInterrupted(fixture, scenario); }

        var resumed = await CrashRecoveryProcess.ResumeAsync(fixture, scenario, operationToken);

        Assert.IsTrue
        (
            crashedProcess.ProcessId != resumed.GetProperty("processId").GetInt32() ||
            crashedProcess.StartTicks != resumed.GetProperty("processStartTicks").GetInt64(),
            "Recovery must run in a fresh process, allowing for OS process-ID reuse."
        );
        Assert.AreEqual(1, resumed.GetProperty("progressResumedCount").GetInt32());
        Assert.AreEqual(0, resumed.GetProperty("progressUnavailableCount").GetInt32());
        Assert.AreEqual(0, resumed.GetProperty("progressCleanupFailedCount").GetInt32());
        Assert.IsTrue(File.Exists(Path.Combine(fixture.RootPath, "resume-finally")));
        Assert.IsFalse(Directory.EnumerateFiles(fixture.ProgressDirectory, "*.sqlite").Any());
        if (backup) { await BackupCrashRecoveryScenario.AssertRecoveredAsync(fixture, scenario); }
        else { await RestoreCrashRecoveryScenario.AssertRecoveredAsync(fixture, scenario); }
    }

    private static async Task RunWorkerAsync(string rootPath, string scenario)
    {
        Assert.IsTrue(Path.IsPathFullyQualified(rootPath));
        var token = Environment.GetEnvironmentVariable(CrashRecoveryProcess.TokenVariable);
        Assert.IsNotNull(token);
        Assert.AreEqual(token, await File.ReadAllTextAsync(Path.Combine(rootPath, "worker-token")));
        var phase = Environment.GetEnvironmentVariable(CrashRecoveryProcess.PhaseVariable);
        Assert.IsTrue(phase is "crash" or "resume");
        var fixture = new CrashRecoveryFixture(rootPath);
        var observer = new CrashRecoveryGate(rootPath, scenario, armed: phase == "crash");
        using var currentProcess = Process.GetCurrentProcess();
        try
        {
            var result = scenario.StartsWith("backup-", StringComparison.Ordinal) ?
                await fixture.BackupAsync(observer) : await fixture.RestoreAsync(observer);
            Assert.AreEqual("resume", phase, "The armed worker returned without reaching its requested boundary.");
            Assert.IsTrue(result.Completed, result.Message);
            Assert.AreEqual(0, result.FailedCount);
            await File.WriteAllTextAsync
            (
                Path.Combine(rootPath, "resume-completed.json"),
                JsonSerializer.Serialize(new
                {
                    scenario,
                    token,
                    processId = Environment.ProcessId,
                    processStartTicks = currentProcess.StartTime.ToUniversalTime().Ticks,
                    progressResumedCount = observer.ProgressResumedCount,
                    progressUnavailableCount = observer.ProgressUnavailableCount,
                    progressCleanupFailedCount = observer.ProgressCleanupFailedCount,
                })
            );
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, phase + "-finally"), "managed unwinding occurred");
        }
    }
}

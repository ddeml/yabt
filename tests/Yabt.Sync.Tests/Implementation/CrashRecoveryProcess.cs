using System.Diagnostics;
using System.Text.Json;

namespace Yabt.Sync.Tests.Implementation;

internal static class CrashRecoveryProcess
{
    internal const string RootVariable = "YABT_PROCESS_CRASH_TEST_ROOT";
    internal const string ScenarioVariable = "YABT_PROCESS_CRASH_TEST_SCENARIO";
    internal const string PhaseVariable = "YABT_PROCESS_CRASH_TEST_PHASE";
    internal const string TokenVariable = "YABT_PROCESS_CRASH_TEST_TOKEN";

    internal static async Task<(int ProcessId, long StartTicks)> KillAtGateAsync
    (
        CrashRecoveryFixture fixture,
        string scenario,
        string token
    )
    {
        using var process = Start(fixture, scenario, token, "crash");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        var readyPath = Path.Combine(fixture.RootPath, "crash-ready.json");
        Process? worker = null;
        try
        {
            while (!File.Exists(readyPath) && !process.HasExited)
            {
                await Task.Delay(25, timeout.Token);
            }
            if (process.HasExited)
            {
                Assert.Fail("The worker exited before its crash boundary. " + await output + await error);
            }
            using var ready = JsonDocument.Parse(await File.ReadAllTextAsync(readyPath, timeout.Token));
            Assert.AreEqual(scenario, ready.RootElement.GetProperty("scenario").GetString());
            var workerProcessId = ready.RootElement.GetProperty("processId").GetInt32();
            Assert.AreNotEqual(Environment.ProcessId, workerProcessId);
            worker = Process.GetProcessById(workerProcessId);
            var workerStartTicks = worker.StartTime.ToUniversalTime().Ticks;

            // Kill only the process tree launched above. No cancellation is sent
            // to the synchronizer, so disposal cannot flush its pending batch.
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token);
            // Waiting for vstest alone does not guarantee that testhost has
            // exited and released its native file handles.
            await worker.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error).WaitAsync(timeout.Token);
            Assert.AreNotEqual(0, process.ExitCode, await output + await error);
            return (workerProcessId, workerStartTicks);
        }
        finally
        {
            try
            {
                await EnsureExitedAsync(process);
                if (worker is not null) { await EnsureExitedAsync(worker); }
                await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally { worker?.Dispose(); }
        }
    }

    internal static async Task<JsonElement> ResumeAsync
    (
        CrashRecoveryFixture fixture,
        string scenario,
        string token
    )
    {
        using var process = Start(fixture, scenario, token, "resume");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error).WaitAsync(timeout.Token);
            Assert.AreEqual(0, process.ExitCode, await output + await error);
            var resultPath = Path.Combine(fixture.RootPath, "resume-completed.json");
            Assert.IsTrue(File.Exists(resultPath), "The requested worker row did not complete.");
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, timeout.Token));
            Assert.AreEqual(scenario, result.RootElement.GetProperty("scenario").GetString());
            Assert.AreEqual(token, result.RootElement.GetProperty("token").GetString());
            return result.RootElement.Clone();
        }
        finally
        {
            await EnsureExitedAsync(process);
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private static Process Start(CrashRecoveryFixture fixture, string scenario, string token, string phase)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(ProcessCrashRecoveryTests).Assembly.Location);
        start.ArgumentList.Add
        (
            "/TestCaseFilter:FullyQualifiedName=" +
                "Yabt.Sync.Tests.ProcessCrashRecoveryTests.FreshProcessRecoversAfterForcedTermination"
        );
        start.Environment[RootVariable] = fixture.RootPath;
        start.Environment[ScenarioVariable] = scenario;
        start.Environment[PhaseVariable] = phase;
        start.Environment[TokenVariable] = token;
        return Process.Start(start) ?? throw new InvalidOperationException("The recovery worker did not start.");
    }

    private static async Task EnsureExitedAsync(Process process)
    {
        if (process.HasExited) { return; }
        process.Kill(entireProcessTree: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
    }
}

using System.Text;
using Azure;

namespace Yabt.Tests.Implementation;

internal static class DeployedServiceDiagnostics
{
    public static async Task<string> RecordFailureAsync(string caseId, string phase, Exception failure)
    {
        var types = new List<string>();
        var statuses = new List<int>();
        for (var current = failure; current is not null; current = current.InnerException)
        {
            types.Add(current.GetType().Name);
            if (current is RequestFailedException azureFailure) { statuses.Add(azureFailure.Status); }
            if (current is HttpRequestException { StatusCode: { } status }) { statuses.Add((int)status); }
        }
        var summary = "Exception types: " + string.Join(" -> ", types) + ".";
        if (statuses.Count != 0) { summary += " HTTP statuses: " + string.Join(", ", statuses) + "."; }
        var privateDirectory = Environment.GetEnvironmentVariable("YABT_SERVICE_ACCEPTANCE_PRIVATE_RESULTS");
        if (string.IsNullOrWhiteSpace(privateDirectory)) { return summary; }
        try
        {
            ValidatePrivateDirectory(privateDirectory);
            var path = Path.Combine(privateDirectory, caseId + ".txt");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            await using var stream = new FileStream(path, options);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync($"Case: {caseId}; phase: {phase}");
            await writer.WriteLineAsync(failure.ToString());
            return summary + " A raw diagnostic was saved in the explicitly configured private results directory.";
        }
        catch (Exception)
        {
            // Test output is deliberately restricted to allowlisted diagnostics. Even a
            // path or permission exception while saving diagnostics might disclose values.
            return summary + " Private diagnostic output failed or was refused; details were suppressed.";
        }
    }

    private static void ValidatePrivateDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        {
            throw new YabtTestsException("Private diagnostic output requires an existing absolute directory.");
        }
        var fullPath = Path.GetFullPath(path);
        var current = new DirectoryInfo(fullPath);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0 ||
                Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, "yabt.sln")))
            {
                throw new YabtTestsException("Private diagnostic output must be outside repositories and linked paths.");
            }
            current = current.Parent;
        }
    }
}

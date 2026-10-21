using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Yabt.Sync.Tests;

[TestClass]
public sealed class RestorePathVerifierUnavailableEntryTests
{
    [TestMethod]
    [DataRow("source", "missing", true)]
    [DataRow("restore", "missing", true)]
    [DataRow("restore", "missing", false)]
    [DataRow("source", "directory", true)]
    [DataRow("restore", "directory", true)]
    [DataRow("source", "file", true)]
    [DataRow("restore", "file", true)]
    [DataRow("both", "link", true)]
    public async Task VerifyAsyncReportsUnavailableEntriesWithoutClassifyingOrTraversingThem
    (
        string unavailableSide,
        string counterpartKind,
        bool informationLogging
    )
    {
        using var fixture = new VerificationFixture(informationLogging);
        const string unavailableName = "a-unavailable";
        await File.WriteAllTextAsync(Path.Combine(fixture.OutsideRoot, "external.txt"), "untouched");
        var externalSnapshot = CaptureFiles(fixture.OutsideRoot);
        if (unavailableSide is "source" or "both")
        {
            await fixture.CreateDirectoryLinkAsync(Path.Combine(fixture.SourceRoot, unavailableName));
        }
        if (unavailableSide is "restore" or "both")
        {
            await fixture.CreateDirectoryLinkAsync(Path.Combine(fixture.RestoreRoot, unavailableName));
        }
        if (unavailableSide != "both")
        {
            var counterpartRoot = unavailableSide == "source" ? fixture.RestoreRoot : fixture.SourceRoot;
            var counterpartPath = Path.Combine(counterpartRoot, unavailableName);
            if (counterpartKind == "directory")
            {
                Directory.CreateDirectory(counterpartPath);
                await File.WriteAllTextAsync(Path.Combine(counterpartPath, "ordinary.txt"), "readable");
            }
            else if (counterpartKind == "file")
            {
                await File.WriteAllTextAsync(counterpartPath, "readable");
            }
        }
        await fixture.WritePairAsync("equal.txt", "same", "same");
        await fixture.WritePairAsync("different.txt", "left", "right");
        await File.WriteAllTextAsync(Path.Combine(fixture.SourceRoot, "source-only.txt"), "source");
        await File.WriteAllTextAsync(Path.Combine(fixture.RestoreRoot, "restore-only.txt"), "restore");

        var result = await fixture.Verifier.VerifyAsync(new(fixture.SourceRoot, fixture.RestoreRoot));

        Assert.IsFalse(result.Identical);
        Assert.AreEqual(1, result.UncomparedItemCount);
        Assert.AreEqual(1, result.SourceOnlyCount);
        Assert.AreEqual(1, result.RestoreOnlyCount);
        Assert.AreEqual(1, result.DifferentCount);
        Assert.AreEqual(2, result.ComparedFileCount);
        Assert.AreEqual(0, result.ComparedDirectoryCount);
        StringAssert.Contains(result.Message, $"- {unavailableName}:");
        string[] unavailableRoles = unavailableSide == "both" ? ["source", "restore"] : [unavailableSide];
        foreach (var role in unavailableRoles)
        {
            var expectedReason = $"cannot safely compare {role} reparse-point item";
            StringAssert.Contains(result.Message, expectedReason);
            if (informationLogging)
            {
                Assert.IsTrue
                (
                    fixture.Logs.Messages.Any(message => message.Contains(expectedReason, StringComparison.Ordinal))
                );
            }
        }
        Assert.IsFalse(result.Message.Contains("external.txt", StringComparison.Ordinal));
        Assert.IsFalse(result.Message.Contains("ordinary.txt", StringComparison.Ordinal));
        Assert.IsFalse
        (
            fixture.Logs.Messages.Any(message => message.Contains("external.txt", StringComparison.Ordinal))
        );
        CollectionAssert.AreEqual(externalSnapshot, CaptureFiles(fixture.OutsideRoot));
    }

    [TestMethod]
    public async Task VerifyAsyncCountsUnavailablePathsWithTheSameNameInDifferentFoldersSeparately()
    {
        using var fixture = new VerificationFixture();
        await fixture.WritePairAsync("left/equal.txt", "left", "left");
        await fixture.WritePairAsync("right/equal.txt", "right", "right");
        await fixture.CreateDirectoryLinkAsync(Path.Combine(fixture.SourceRoot, "left", "unavailable"));
        await fixture.CreateDirectoryLinkAsync(Path.Combine(fixture.RestoreRoot, "right", "unavailable"));

        var result = await fixture.Verifier.VerifyAsync(new(fixture.SourceRoot, fixture.RestoreRoot));

        Assert.IsFalse(result.Identical);
        Assert.AreEqual(2, result.UncomparedItemCount);
        Assert.AreEqual(0, result.SourceOnlyCount);
        Assert.AreEqual(0, result.RestoreOnlyCount);
        Assert.AreEqual(0, result.DifferentCount);
        Assert.AreEqual(2, result.ComparedFileCount);
        Assert.AreEqual(2, result.ComparedDirectoryCount);
        StringAssert.Contains(result.Message, "- left/unavailable:");
        StringAssert.Contains(result.Message, "- right/unavailable:");
    }

    [TestMethod]
    public async Task VerifyAsyncCountsAnUnreadableFilePairOnceAndRetainsBothFailureReasons()
    {
        using var fixture = new VerificationFixture();
        const string relativePath = "locked.bin";
        await fixture.WritePairAsync(relativePath, "content", "content");
        await fixture.WritePairAsync("different.txt", "left", "right");
        var sourcePath = Path.Combine(fixture.SourceRoot, relativePath);
        var restorePath = Path.Combine(fixture.RestoreRoot, relativePath);
        await using var sourceLock = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.None);
        await using var restoreLock = new FileStream(restorePath, FileMode.Open, FileAccess.Read, FileShare.None);
        var sourceReason = CaptureReadFailureReason(sourcePath);
        var restoreReason = CaptureReadFailureReason(restorePath);

        var result = await fixture.Verifier.VerifyAsync(new(fixture.SourceRoot, fixture.RestoreRoot));

        Assert.IsFalse(result.Identical);
        Assert.AreEqual(1, result.UncomparedItemCount);
        Assert.AreEqual(0, result.SourceOnlyCount);
        Assert.AreEqual(0, result.RestoreOnlyCount);
        Assert.AreEqual(1, result.DifferentCount);
        Assert.AreEqual(2, result.ComparedFileCount);
        StringAssert.Contains(result.Message, $"could not open source file '{sourcePath}'");
        StringAssert.Contains(result.Message, $"could not open restore file '{restorePath}'");
        StringAssert.Contains(result.Message, sourceReason);
        StringAssert.Contains(result.Message, restoreReason);
        Assert.AreEqual
        (
            2,
            fixture.Logs.Messages.Count(message =>
                message.Contains("could not be compared: could not open", StringComparison.Ordinal))
        );
    }

    private static string[] CaptureFiles(string rootPath) => Directory
        .EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
        .Select(path => $"{Path.GetRelativePath(rootPath, path)}:{Convert.ToHexString(File.ReadAllBytes(path))}")
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string CaptureReadFailureReason(string path)
    {
        try
        {
            using var unexpectedlyReadableFile = File.OpenRead(path);
            Assert.Fail($"Expected '{path}' to be unreadable while exclusively locked.");
            return string.Empty;
        }
        catch (IOException ex)
        {
            return ex.Message;
        }
    }

    private sealed class VerificationFixture : IDisposable
    {
        private const string FixturePrefix = "yabt-restore-unavailable-tests-";
        private readonly List<string> _directoryLinks = [];
        private readonly ServiceProvider _services;

        public VerificationFixture(bool informationLogging = true)
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"{FixturePrefix}{Guid.NewGuid():N}");
            SourceRoot = Path.Combine(RootPath, "source");
            RestoreRoot = Path.Combine(RootPath, "restore");
            OutsideRoot = Path.Combine(RootPath, "outside");
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(RestoreRoot);
            Directory.CreateDirectory(OutsideRoot);
            var services = new ServiceCollection();
            if (informationLogging)
            {
                services.AddSingleton<ILoggerFactory>(Logs);
                services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            }
            else
            {
                services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            }
            services.AddYabtSync();
            _services = services.BuildServiceProvider();
        }

        public string RootPath { get; }

        public string SourceRoot { get; }

        public string RestoreRoot { get; }

        public string OutsideRoot { get; }

        public InformationCaptureFactory Logs { get; } = new();

        public IRestorePathVerifier Verifier => _services.GetRequiredService<IRestorePathVerifier>();

        public async Task WritePairAsync(string relativePath, string sourceContent, string restoreContent)
        {
            var nativeRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var sourcePath = Path.Combine(SourceRoot, nativeRelativePath);
            var restorePath = Path.Combine(RestoreRoot, nativeRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(restorePath)!);
            await File.WriteAllTextAsync(sourcePath, sourceContent);
            await File.WriteAllTextAsync(restorePath, restoreContent);
        }

        public async Task CreateDirectoryLinkAsync(string linkPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            if (OperatingSystem.IsWindows())
            {
                var script = $"New-Item -ItemType Junction -Path '{linkPath.Replace("'", "''")}' " +
                    $"-Value '{OutsideRoot.Replace("'", "''")}' -ErrorAction Stop | Out-Null";
                var startInfo = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-EncodedCommand");
                startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
                using var process = Process.Start(startInfo) ??
                    throw new InvalidOperationException("Could not start PowerShell to create a test junction.");
                var error = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.AreEqual
                (
                    0,
                    process.ExitCode,
                    $"Creating the Windows junction failed: {await error} {await output}"
                );
            }
            else
            {
                Directory.CreateSymbolicLink(linkPath, OutsideRoot);
            }

            _directoryLinks.Add(linkPath);
            Assert.AreNotEqual((FileAttributes)0, File.GetAttributes(linkPath) & FileAttributes.ReparsePoint);
        }

        public void Dispose()
        {
            _services.Dispose();
            var fullRoot = Path.GetFullPath(RootPath);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            Assert.AreEqual(temporaryRoot, Path.GetDirectoryName(fullRoot));
            Assert.IsTrue(Path.GetFileName(fullRoot).StartsWith(FixturePrefix, StringComparison.Ordinal));
            foreach (var linkPath in Enumerable.Reverse(_directoryLinks))
            {
                Assert.IsTrue
                (
                    Path.GetFullPath(linkPath).StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                );
                Directory.Delete(linkPath, recursive: false);
            }
            if (Directory.Exists(fullRoot)) { Directory.Delete(fullRoot, recursive: true); }
        }
    }

    private sealed class InformationCaptureFactory : ILoggerFactory
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new InformationCaptureLogger(Messages);

        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

        public void Dispose() { }

        private sealed class InformationCaptureLogger(ConcurrentQueue<string> _messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>
            (
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            )
            {
                if (IsEnabled(logLevel)) { _messages.Enqueue(formatter(state, exception)); }
            }
        }
    }
}

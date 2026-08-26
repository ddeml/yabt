using Yabt.Cli.Implementation;

namespace Yabt.Cli.Tests;

[TestClass]
public sealed class LogFilePathResolverTests
{
    [TestMethod]
    public void ResolveMakesExplicitRelativePathRelativeToCurrentDirectory()
    {
        var currentDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "yabt-current"));
        var request = new LogFileRequest(true, Path.Combine("logs", "run.log"));

        var destination = LogFilePathResolver.Resolve
        (
            request,
            currentDirectory,
            CreateEnvironment(LogFilePlatform.Windows),
            DateTimeOffset.UnixEpoch,
            42,
            Guid.Empty
        );

        Assert.AreEqual
        (
            Path.Combine(currentDirectory, "logs", "run.log"),
            destination.Path
        );
    }

    [TestMethod]
    public void GetDefaultDirectoryUsesWindowsLocalApplicationData()
    {
        var root = GetTestRoot("windows-local-data");

        var directory = LogFilePathResolver.GetDefaultDirectory
        (
            new(LogFilePlatform.Windows, root, null, null)
        );

        Assert.AreEqual(Path.Combine(root, "Yabt", "Logs"), directory);
    }

    [TestMethod]
    public void GetDefaultDirectoryUsesLinuxXdgStateHome()
    {
        var xdgStateHome = GetTestRoot("xdg-state");

        var directory = LogFilePathResolver.GetDefaultDirectory
        (
            new(LogFilePlatform.Linux, null, xdgStateHome, GetTestRoot("home"))
        );

        Assert.AreEqual(Path.Combine(xdgStateHome, "yabt", "logs"), directory);
    }

    [TestMethod]
    public void GetDefaultDirectoryFallsBackToLinuxUserStateDirectory()
    {
        var userProfile = GetTestRoot("linux-home");

        var directory = LogFilePathResolver.GetDefaultDirectory
        (
            new(LogFilePlatform.Linux, null, "relative-xdg-path", userProfile)
        );

        Assert.AreEqual
        (
            Path.Combine(userProfile, ".local", "state", "yabt", "logs"),
            directory
        );
    }

    [TestMethod]
    public void GetDefaultDirectoryUsesMacUserLogsDirectory()
    {
        var userProfile = GetTestRoot("mac-home");

        var directory = LogFilePathResolver.GetDefaultDirectory
        (
            new(LogFilePlatform.MacOS, null, null, userProfile)
        );

        Assert.AreEqual
        (
            Path.Combine(userProfile, "Library", "Logs", "Yabt"),
            directory
        );
    }

    [TestMethod]
    public void ResolveCreatesUniquePerInvocationDefaultName()
    {
        var localData = GetTestRoot("default-name");
        var timestamp = new DateTimeOffset(2026, 8, 26, 12, 34, 56, TimeSpan.Zero)
            .AddTicks(1_234_567);
        var firstId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var secondId = Guid.Parse("10112233-4455-6677-8899-aabbccddeeff");
        var environment = new LogFileEnvironment(LogFilePlatform.Windows, localData, null, null);

        var first = LogFilePathResolver.Resolve
        (
            new(true, null),
            GetTestRoot("current"),
            environment,
            timestamp,
            42,
            firstId
        );
        var second = LogFilePathResolver.Resolve
        (
            new(true, null),
            GetTestRoot("current"),
            environment,
            timestamp,
            42,
            secondId
        );

        Assert.AreEqual
        (
            Path.Combine
            (
                localData,
                "Yabt",
                "Logs",
                "yabt-20260826T1234561234567Z-42-00112233445566778899aabbccddeeff.log"
            ),
            first.Path
        );
        Assert.AreNotEqual(first.Path, second.Path);
    }

    private static LogFileEnvironment CreateEnvironment(LogFilePlatform platform) => new
    (
        platform,
        GetTestRoot("local-data"),
        GetTestRoot("xdg-state"),
        GetTestRoot("home")
    );

    private static string GetTestRoot(string name) =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "yabt-cli-tests", name));
}

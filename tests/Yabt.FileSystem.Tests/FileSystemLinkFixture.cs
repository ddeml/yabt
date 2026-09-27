using System.Diagnostics;

namespace Yabt.FileSystem.Tests;

internal sealed class FileSystemLinkFixture : IDisposable
{
    private readonly List<string> _links = [];

    public FileSystemLinkFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "Yabt.FileSystem.LinkTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Archive = Directory.CreateDirectory(Path.Combine(Root, "archive")).FullName;
        Outside = Directory.CreateDirectory(Path.Combine(Root, "outside")).FullName;
    }

    public string Root { get; }
    public string Archive { get; }
    public string Outside { get; }

    public void LinkDirectory(string path, string target)
    {
        CreateDirectoryLink(path, target);
        _links.Add(path);
    }

    public static void CreateDirectoryLink(string path, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, target);
            return;
        }

        // Junctions do not require the symbolic-link privilege on Windows.
        var start = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(path);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Junction creation did not start.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, $"Junction creation failed: {output} {error}");
    }

    public void Dispose()
    {
        // Unlink each known fixture link before recursively deleting only our private root.
        foreach (var link in _links.AsEnumerable().Reverse())
        {
            if (Directory.Exists(link)) { Directory.Delete(link, recursive: false); }
        }
        Directory.Delete(Root, recursive: true);
    }
}

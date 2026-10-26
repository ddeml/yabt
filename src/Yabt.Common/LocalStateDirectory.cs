namespace Yabt.Common;

/// <summary>Machine-local per-user state, independent of archive providers.</summary>
public static class LocalStateDirectory
{
    public static string GetProgressDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(RequireHome(Environment.SpecialFolder.LocalApplicationData), "Yabt", "Progress");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(RequireHome(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Yabt", "Progress");
        }

        var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var root = !string.IsNullOrWhiteSpace(stateHome) && Path.IsPathFullyQualified(stateHome) ? stateHome :
            Path.Combine(RequireHome(Environment.SpecialFolder.UserProfile), ".local", "state");
        return Path.Combine(root, "yabt", "progress");
    }

    public static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(path); }
        else { Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    private static string RequireHome(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        return !string.IsNullOrWhiteSpace(path) ? path :
            throw new IOException("A per-user local state directory is unavailable. Configure Sync:ProgressDirectory.");
    }
}

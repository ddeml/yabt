namespace Yabt.FileSystem.Implementation;

internal sealed record FileSystemRestoreStagingFile(string Path, Action<string> ReleaseToken);

namespace Yabt.Sync.Implementation;

internal sealed record RestoreDestinationState
(
    IReadOnlyDictionary<string, RestoreDestinationFile> Files,
    IReadOnlySet<string> Directories
);

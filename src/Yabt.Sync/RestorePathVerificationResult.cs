namespace Yabt.Sync;

public sealed record RestorePathVerificationResult
(
    bool Identical,
    string Message,
    int SourceOnlyCount = default,
    int DifferentCount = default,
    int RestoreOnlyCount = default,
    int ComparedFileCount = default,
    int ComparedDirectoryCount = default,
    int UncomparedItemCount = default
);

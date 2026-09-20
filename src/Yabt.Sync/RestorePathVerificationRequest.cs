namespace Yabt.Sync;

public sealed record RestorePathVerificationRequest
(
    string SourceRoot,
    string RestoreRoot
);

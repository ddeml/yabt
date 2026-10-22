namespace Yabt.Sync.Implementation;

internal sealed record StreamComparison
(
    bool Same,
    long SourceLength,
    string? SourceContentHash
);

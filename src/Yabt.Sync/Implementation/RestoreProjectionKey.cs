namespace Yabt.Sync.Implementation;

internal sealed record RestoreProjectionKey
(
    string LogicalPath,
    string Format,
    int FormatVersion,
    string ProjectionId
);

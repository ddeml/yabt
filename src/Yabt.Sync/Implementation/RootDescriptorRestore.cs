namespace Yabt.Sync.Implementation;

internal sealed record RootDescriptorRestore
(
    bool NeedsWrite,
    bool MoveExistingToHistory
)
{
    public static RootDescriptorRestore None { get; } = new(
        NeedsWrite: false,
        MoveExistingToHistory: false);
}

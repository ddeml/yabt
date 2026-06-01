namespace Yabt.Sync.Implementation;

internal sealed class ArchiveSyncSummary
{
    public int NewCount { get; private set; }

    public int ChangedCount { get; private set; }

    public int ExtraCount { get; private set; }

    public int UnchangedCount { get; private set; }

    public void AddNew()
    {
        NewCount++;
    }

    public void AddChanged()
    {
        ChangedCount++;
    }

    public void AddExtra()
    {
        ExtraCount++;
    }

    public void AddUnchanged()
    {
        UnchangedCount++;
    }
}

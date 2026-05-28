using System.Collections.Frozen;

namespace Yabt.Cli.Implementation;

internal static class YabtCliCommandNames
{
    public const string Sync = "sync";
    public const string Restore = "restore";
    public const string Scan = "scan";
    public const string Verify = "verify";
    public const string Pack = "pack";
    public const string Reconcile = "reconcile";

    public static readonly FrozenSet<string> Known = new[]
    {
        Sync,
        Restore,
        Scan,
        Verify,
        Pack,
        Reconcile,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}

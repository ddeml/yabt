using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Core.Implementation;

internal sealed class ScopedObjectPathComparer
(
    IObjectStoreNamespace _parent,
    ObjectPath _prefix
) : StringComparer
{
    public override int Compare(string? x, string? y)
    {
        if (x is null) { return y is null ? 0 : -1; }
        if (y is null) { return 1; }
        return _parent.PathComparer.Compare(ToParentPath(x), ToParentPath(y));
    }

    public override bool Equals(string? x, string? y)
    {
        if (x is null || y is null) { return x is null && y is null; }
        return _parent.PathComparer.Equals(ToParentPath(x), ToParentPath(y));
    }

    public override int GetHashCode(string obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return _parent.PathComparer.GetHashCode(ToParentPath(obj));
    }

    private string ToParentPath(string path) => _prefix.Combine(ObjectPath.Parse(path)).Value;
}

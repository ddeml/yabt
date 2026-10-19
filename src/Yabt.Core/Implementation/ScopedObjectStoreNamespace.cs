using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Core.Implementation;

internal sealed class ScopedObjectStoreNamespace
(
    IObjectStoreNamespace _parent,
    ObjectPath _prefix
) : ObjectStoreNamespace
{
    public override StringComparer PathComparer { get; } = new ScopedObjectPathComparer(_parent, _prefix);

    public override void ValidatePath(ObjectPath path) => _parent.ValidatePath(_prefix.Combine(path));

    public override IObjectStoreNamespace AfterRemoving(IEnumerable<ObjectPath> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return _parent.AfterRemoving(paths.Select(path => _prefix.Combine(path))).Scope(_prefix);
    }
}

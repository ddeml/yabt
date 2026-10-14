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
}

using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Core.Implementation;

internal sealed class OrdinalObjectStoreNamespace : ObjectStoreNamespace
{
    public override StringComparer PathComparer => StringComparer.Ordinal;

    public override void ValidatePath(ObjectPath path)
    {
        // ObjectPath already enforces the canonical namespace grammar.
    }
}

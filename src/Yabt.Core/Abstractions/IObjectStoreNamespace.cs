using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

/// <summary>
/// Defines which canonical names a store can represent and which paths identify the same destination.
/// </summary>
public interface IObjectStoreNamespace
{
    /// <summary>Compares canonical paths relative to this namespace, including their ancestor context.</summary>
    StringComparer PathComparer { get; }

    void ValidatePath(ObjectPath path);

    /// <summary>Returns namespace rules for paths relative to the specified directory prefix.</summary>
    IObjectStoreNamespace Scope(ObjectPath prefix);

    /// <summary>Validates desired files, directories, and every implicit ancestor directory.</summary>
    void ValidateTree(IEnumerable<ObjectPath> files, IEnumerable<ObjectPath> directories);
}

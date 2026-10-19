using Yabt.Core.Implementation;
using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

public abstract class ObjectStoreNamespace : IObjectStoreNamespace
{
    public static IObjectStoreNamespace Ordinal { get; } = new OrdinalObjectStoreNamespace();

    public abstract StringComparer PathComparer { get; }

    public abstract void ValidatePath(ObjectPath path);

    public virtual IObjectStoreNamespace AfterRemoving(IEnumerable<ObjectPath> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return this;
    }

    public virtual IObjectStoreNamespace Scope(ObjectPath prefix)
    {
        var ancestor = prefix;
        while (true)
        {
            ValidatePath(ancestor);
            if (ancestor.IsRoot) { break; }
            ancestor = ancestor.Parent;
        }

        return prefix.IsRoot ? this : new ScopedObjectStoreNamespace(this, prefix);
    }

    public void ValidateTree(IEnumerable<ObjectPath> files, IEnumerable<ObjectPath> directories)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(directories);

        var filePaths = new Dictionary<string, ObjectPath>(PathComparer);
        var directoryPaths = new Dictionary<string, ObjectPath>(PathComparer);
        foreach (var file in files)
        {
            if (file.IsRoot)
            {
                throw new YabtCoreException("The object-store root cannot be represented as a file.");
            }

            ValidatePath(file);
            if (filePaths.TryGetValue(file.Value, out var existingFile))
            {
                throw new YabtCoreException
                (
                    $"Files '{existingFile}' and '{file}' identify the same destination path."
                );
            }

            if (directoryPaths.TryGetValue(file.Value, out var existingDirectory))
            {
                throw new YabtCoreException
                (
                    $"File '{file}' and directory '{existingDirectory}' identify the same destination path."
                );
            }

            filePaths.Add(file.Value, file);
            AddDirectoryAndAncestors(file.Parent);
        }

        foreach (var directory in directories)
        {
            AddDirectoryAndAncestors(directory);
        }

        void AddDirectoryAndAncestors(ObjectPath path)
        {
            while (true)
            {
                ValidatePath(path);
                if (filePaths.TryGetValue(path.Value, out var existingFile))
                {
                    throw new YabtCoreException
                    (
                        $"Directory '{path}' and file '{existingFile}' identify the same destination path."
                    );
                }

                if (directoryPaths.TryGetValue(path.Value, out var existingDirectory))
                {
                    if (path != existingDirectory)
                    {
                        throw new YabtCoreException
                        (
                            $"Directories '{existingDirectory}' and '{path}' identify the same destination path."
                        );
                    }

                    return;
                }

                directoryPaths.Add(path.Value, path);
                if (path.IsRoot) { return; }
                path = path.Parent;
            }
        }
    }
}

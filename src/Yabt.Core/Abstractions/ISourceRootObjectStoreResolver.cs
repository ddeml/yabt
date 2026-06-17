namespace Yabt.Core.Abstractions;

public interface ISourceRootObjectStoreResolver
{
    IObjectStore ResolveSourceRoot(string rootPath);
}

namespace Yabt.Core.Abstractions;

/// <summary>Optional provider capability for operations that need a native local storage boundary.</summary>
public interface ILocalObjectStore
{
    string RootPath { get; }
}

namespace Yabt.Core.Abstractions;

public interface IObjectStoreStagingArea : IAsyncDisposable
{
    /// <summary>Allocates an opaque staging token. Consumers must not interpret or manipulate it as a path.</summary>
    string CreateFileToken();

    Stream OpenWrite(string stagingToken);

    void DeleteFile(string stagingToken);
}

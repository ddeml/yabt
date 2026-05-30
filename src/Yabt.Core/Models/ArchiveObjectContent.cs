namespace Yabt.Core.Models;

public sealed record ArchiveObjectContent
(
    Stream Content,
    string ContentType = "application/octet-stream",
    IReadOnlyDictionary<string, string>? Metadata = default
) : IDisposable, IAsyncDisposable
{
    public void Dispose()
    {
        Content.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        return Content.DisposeAsync();
    }
}

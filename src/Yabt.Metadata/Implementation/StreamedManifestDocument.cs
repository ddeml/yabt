using System.Text.Json;

namespace Yabt.Metadata.Implementation;

internal sealed class StreamedManifestDocument<TEntry>
(
    JsonDocument header,
    TemporaryManifestEntries<TEntry> entries,
    bool hasEntryProjectionProperty
) : IDisposable
{
    private TemporaryManifestEntries<TEntry>? _entries = entries;

    public JsonDocument Header { get; } = header;

    public TemporaryManifestEntries<TEntry> Entries => _entries ??
        throw new InvalidOperationException("Manifest entry ownership was already transferred.");

    public bool HasEntryProjectionProperty { get; } = hasEntryProjectionProperty;

    public void TransferEntries() => _entries = null;

    public void Dispose()
    {
        Header.Dispose();
        _entries?.Dispose();
        _entries = null;
    }
}

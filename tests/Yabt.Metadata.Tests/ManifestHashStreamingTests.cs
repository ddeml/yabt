using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Yabt.Core.Models;

namespace Yabt.Metadata.Tests;

[TestClass]
public sealed class ManifestHashStreamingTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 22, 12, 34, 56, TimeSpan.Zero);
    private static readonly string ContentHash = ArchiveHash.Compute("payload"u8);

    [TestMethod]
    [DataRow("change")]
    [DataRow("history")]
    [DataRow("logical")]
    [DataRow("package")]
    [DataRow("reference")]
    public async Task LargeCanonicalHashesMatchTheExistingCompactJsonRepresentation(string kind)
    {
        using var services = new ServiceCollection().AddYabtMetadata().BuildServiceProvider();
        using var output = new RecordingWriteStream();
        var hash = await CreateAndWriteAsync(services, kind, output);
        output.Position = 0;
        using var document = await JsonDocument.ParseAsync(output);
        using var canonical = new MemoryStream();
        using (var writer = new Utf8JsonWriter(canonical))
        {
            writer.WriteStartObject();
            var propertyNames = kind switch
            {
                "change" => new[]
                {
                    "documentType", "schemaVersion", "rootFormat", "rootDescriptorContentHash",
                    "rootDescriptorContentLength", "entries",
                },
                "package" =>
                [
                    "documentType", "schemaVersion", "sourcePath", "createdAtUtc", "format",
                    "formatVersion", "projectionId", "packageName", "policy", "entries", "totalBytes",
                ],
                "reference" => ["documentType", "schemaVersion", "message", "entry"],
                _ => ["documentType", "schemaVersion", "entries"],
            };
            foreach (var name in propertyNames)
            {
                if (!document.RootElement.TryGetProperty(name, out var value)) { continue; }
                writer.WritePropertyName(name);
                WriteCanonicalValue(writer, value, kind is "history" or "reference");
            }
            writer.WriteEndObject();
        }

        Assert.AreEqual(ArchiveHash.Compute(canonical.ToArray()), hash);
        Assert.IsGreaterThan(1024 * 1024, output.Length);
        Assert.IsGreaterThan(1, output.WriteCalls);
        Assert.IsLessThan(64 * 1024, output.MaximumWriteLength);
    }

    private static void WriteCanonicalValue(Utf8JsonWriter writer, JsonElement value, bool historyTimestamps)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                if (historyTimestamps && property.Name == "lastModifiedUtc")
                {
                    // The existing canonical history writer uses the typed timestamp overload,
                    // whose '+' is not escaped as it would be when rewriting a JSON string.
                    writer.WriteString(property.Name, property.Value.GetDateTimeOffset());
                    continue;
                }
                writer.WritePropertyName(property.Name);
                WriteCanonicalValue(writer, property.Value, historyTimestamps);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) { WriteCanonicalValue(writer, item, historyTimestamps); }
            writer.WriteEndArray();
        }
        else
        {
            value.WriteTo(writer);
        }
    }

    private static async Task<string> CreateAndWriteAsync(ServiceProvider services, string kind, Stream output)
    {
        var paths = Enumerable.Range(0, 12_000).Select(index => $"folder/ä-{index:D6}.txt");
        switch (kind)
        {
            case "change":
            {
                var changeSerializer = services.GetRequiredService<IChangeManifestSerializer>();
                using var change = changeSerializer.Create
                (
                    paths.Select(path => new ArchiveChangeManifestEntry
                    (
                        path,
                        ArchiveChangeFingerprint.Create(7, Timestamp),
                        ContentHash: ContentHash
                    )),
                    "mirror",
                    ContentHash,
                    123
                );
                await changeSerializer.WriteAsync(change, output);
                return change.ManifestHash;
            }

            case "history":
            {
                var historySerializer = services.GetRequiredService<IHistoryManifestSerializer>();
                using var history = historySerializer.Create(paths.Select(path => HistoryEntry(path)));
                await historySerializer.WriteAsync(history, output);
                return history.ManifestHash;
            }

            case "logical":
            {
                var logicalSerializer = services.GetRequiredService<ILogicalStateManifestSerializer>();
                using var logical = logicalSerializer.Create(paths.Select(path => new ArchiveLogicalStateManifestEntry
                (
                    path,
                    ArchiveChangeFingerprint.Create(7, Timestamp),
                    ContentHash
                )));
                await logicalSerializer.WriteAsync(logical, output);
                return logical.ManifestHash;
            }

            case "package":
            {
                var packageSerializer = services.GetRequiredService<IManifestSerializer>();
                using var package = packageSerializer.Create
                (
                    "folder",
                    Timestamp,
                    "zip",
                    1,
                    ContentHash,
                    $"folder.{ArchiveHash.FormatFileNameToken(ContentHash)}.zip",
                    new FolderPolicy("zip"),
                    paths.Select(path => new ArchiveManifestEntry
                    (
                        ArchiveManifestEntryKinds.File,
                        path,
                        path,
                        7,
                        Timestamp,
                        ContentHash
                    ))
                );
                await packageSerializer.WriteAsync(package, output);
                return package.ManifestHash;
            }

            case "reference":
            {
                var metadata = Enumerable.Range(0, 24_000).ToDictionary
                (
                    index => $"property-{index:D6}",
                    index => $"value-{index:D6}-with-enough-characters-for-a-large-document",
                    StringComparer.Ordinal
                );
                var referenceSerializer = services.GetRequiredService<IHistoryReferenceSerializer>();
                var reference = referenceSerializer.Create
                (
                    HistoryEntry("file.txt") with
                    {
                        Representation = ArchiveHistoryEntryRepresentation.Reference,
                        StoredRelativePath = "file.txt.yabt-ref.json",
                        Metadata = metadata,
                    }
                );
                await referenceSerializer.WriteAsync(reference, output);
                return reference.ReferenceHash;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static ArchiveHistoryManifestEntry HistoryEntry(string path) => new
    (
        path,
        path,
        ArchiveHistoryEntryRepresentation.Materialized,
        7,
        ContentHash,
        Timestamp,
        "text/plain",
        new Dictionary<string, string> { ["origin"] = "source" }
    );

    private sealed class RecordingWriteStream : MemoryStream
    {
        public int WriteCalls { get; private set; }
        public int MaximumWriteLength { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            RecordWrite(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            RecordWrite(buffer.Length);
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            RecordWrite(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        private void RecordWrite(int length)
        {
            WriteCalls++;
            MaximumWriteLength = Math.Max(MaximumWriteLength, length);
        }
    }
}

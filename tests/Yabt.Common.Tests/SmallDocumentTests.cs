namespace Yabt.Common.Tests;

[TestClass]
public sealed class SmallDocumentTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RejectsOversizedDocumentsWithoutTrustingLength(bool unknownLength)
    {
        using var source = new DocumentTestStream(4096, unknownLength, chunkSize: 7);

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>
        (
            () => SmallDocument.ReadAllBytesAsync(source, 100, "root.json")
        );

        Assert.AreEqual(101, source.BytesRead);
        StringAssert.Contains(error.Message, "root.json");
        StringAssert.Contains(error.Message, "100 bytes");
        Assert.IsTrue(source.CanRead);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(99)]
    [DataRow(100)]
    public async Task AcceptsContentAtOrBelowTheLimit(int length)
    {
        using var source = new DocumentTestStream(length, unknownLength: true, chunkSize: 13);

        var content = await SmallDocument.ReadAllBytesAsync(source, 100, "policy.json");

        Assert.HasCount(length, content);
        Assert.IsTrue(content.All(value => value == (byte)' '));
        Assert.AreEqual(length, source.BytesRead);
    }

    [TestMethod]
    public async Task CancellationStopsBeforeReadingEvenIfSourceIgnoresCancellation()
    {
        using var source = new DocumentTestStream(4096, unknownLength: true);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>
        (
            () => SmallDocument.ReadAllBytesAsync(source, 100, "policy.json", cancellation.Token)
        );

        Assert.AreEqual(0, source.BytesRead);
    }

    private sealed class DocumentTestStream
    (
        int length,
        bool unknownLength,
        int chunkSize = int.MaxValue
    ) : Stream
    {
        public int BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => unknownLength ? throw new NotSupportedException() : 1;
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync
        (
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            var count = Math.Min(Math.Min(buffer.Length, length - BytesRead), chunkSize);
            buffer.Span[..count].Fill((byte)' ');
            BytesRead += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

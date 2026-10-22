namespace Yabt.Common.Implementation;

internal sealed class SpillableTemporaryContent
(
    TemporaryContentFactory _factory,
    int _memoryThreshold,
    string _temporaryDirectory
) : TemporaryContent
{
    private const int SegmentSize = 64 * 1024;
    private readonly Lock _gate = new();
    private readonly List<byte[]> _segments = [];
    private FileStream? _file;
    private long _length;
    private long _position;
    private int _capacity;
    private int _readers;
    private bool _sealed;
    private bool _disposed;
    private bool _backingReleased;

    public override bool IsSealed => _sealed;

    public override bool IsFileBacked => _file is not null;

    public override bool CanRead => !_disposed;

    public override bool CanSeek => !_disposed;

    public override bool CanWrite => !_disposed && !_sealed;

    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return _file?.Length ?? _length;
        }
    }

    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return _file?.Position ?? _position;
        }
        set
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            if (_file is not null) { _file.Position = value; }
            else { _position = value; }
        }
    }

    public override async ValueTask SealAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_sealed) { return; }
        if (_file is not null) { await _file.FlushAsync(cancellationToken); }
        lock (_gate) { _sealed = true; }
    }

    public override Stream OpenRead()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_sealed) { throw new InvalidOperationException("Temporary content must be sealed before opening readers."); }

            Stream reader = _file is null ?
                new TemporaryContentMemoryReadStream(this, _length) :
                new FileStream
                (
                    _file.Name,
                    new FileStreamOptions
                    {
                        Access = FileAccess.Read,
                        Mode = FileMode.Open,
                        Share = FileShare.ReadWrite | FileShare.Delete,
                        BufferSize = 1,
                        Options = FileOptions.Asynchronous | FileOptions.RandomAccess
                    }
                );
            _readers++;
            return new TemporaryContentReadLease(reader, this);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (_file is not null) { return _file.Read(buffer); }
        var read = ReadMemory(_position, buffer);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync
    (
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken = default
    ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync
    (
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return _file is null ? ValueTask.FromResult(Read(buffer.Span)) : _file.ReadAsync(buffer, cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfNotWritable();
        if (_file is null && !TryGrowMemory(checked(_position + buffer.Length))) { Spill(); }
        if (_file is not null) { _file.Write(buffer); }
        else { WriteMemory(buffer); }
    }

    public override Task WriteAsync
    (
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken = default
    ) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync
    (
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfNotWritable();
        cancellationToken.ThrowIfCancellationRequested();
        if (_file is null && !TryGrowMemory(checked(_position + buffer.Length)))
        {
            await SpillAsync(cancellationToken);
        }
        if (_file is not null) { await _file.WriteAsync(buffer, cancellationToken); }
        else { WriteMemory(buffer.Span); }
    }

    public override void SetLength(long value)
    {
        ThrowIfNotWritable();
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        if (_file is null && !TryGrowMemory(value)) { Spill(); }
        if (_file is not null) { _file.SetLength(value); }
        else
        {
            if (value < _length) { ClearMemory(value, _length - value); }
            _length = value;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        var position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(Position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (position < 0) { throw new IOException("Cannot seek before the start of temporary content."); }
        Position = position;
        return position;
    }

    public override void Flush()
    {
        ThrowIfDisposed();
        _file?.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return _file?.FlushAsync(cancellationToken) ?? Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            FileStream? backing;
            lock (_gate)
            {
                _disposed = true;
                backing = ReleaseBackingIfUnused();
            }
            backing?.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        FileStream? backing;
        lock (_gate)
        {
            _disposed = true;
            backing = ReleaseBackingIfUnused();
        }
        if (backing is not null) { await backing.DisposeAsync(); }
        GC.SuppressFinalize(this);
    }

    internal int ReadMemory(long position, Span<byte> buffer)
    {
        if (position >= _length) { return 0; }
        var count = (int)Math.Min(buffer.Length, _length - position);
        var copied = 0;
        while (copied < count)
        {
            var segmentIndex = (int)(position / SegmentSize);
            var segmentOffset = (int)(position % SegmentSize);
            var segment = _segments[segmentIndex];
            var take = Math.Min(count - copied, segment.Length - segmentOffset);
            segment.AsSpan(segmentOffset, take).CopyTo(buffer[copied..]);
            position += take;
            copied += take;
        }
        return count;
    }

    internal void ReleaseReader()
    {
        FileStream? backing;
        lock (_gate)
        {
            _readers--;
            backing = ReleaseBackingIfUnused();
        }
        backing?.Dispose();
    }

    internal async ValueTask ReleaseReaderAsync()
    {
        FileStream? backing;
        lock (_gate)
        {
            _readers--;
            backing = ReleaseBackingIfUnused();
        }
        if (backing is not null) { await backing.DisposeAsync(); }
    }

    private bool TryGrowMemory(long requiredLength)
    {
        if (requiredLength <= _capacity) { return true; }
        if (requiredLength > _memoryThreshold) { return false; }
        var desiredCapacity = (int)Math.Min
        (
            _memoryThreshold,
            ((requiredLength + SegmentSize - 1) / SegmentSize) * SegmentSize
        );
        var growth = desiredCapacity - _capacity;
        if (!_factory.TryReserve(growth)) { return false; }

        var previousCount = _segments.Count;
        try
        {
            var allocated = _capacity;
            while (allocated < desiredCapacity)
            {
                var segmentLength = Math.Min(SegmentSize, desiredCapacity - allocated);
                _segments.Add(new byte[segmentLength]);
                allocated += segmentLength;
            }
            _capacity = desiredCapacity;
            return true;
        }
        catch (Exception)
        {
            _segments.RemoveRange(previousCount, _segments.Count - previousCount);
            _factory.Release(growth);
            throw;
        }
    }

    private void WriteMemory(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty) { return; }
        if (_position > _length) { ClearMemory(_length, _position - _length); }
        var copied = 0;
        while (copied < buffer.Length)
        {
            var segmentIndex = (int)(_position / SegmentSize);
            var segmentOffset = (int)(_position % SegmentSize);
            var segment = _segments[segmentIndex];
            var take = Math.Min(buffer.Length - copied, segment.Length - segmentOffset);
            buffer.Slice(copied, take).CopyTo(segment.AsSpan(segmentOffset));
            copied += take;
            _position += take;
        }
        _length = Math.Max(_length, _position);
    }

    private void ClearMemory(long start, long length)
    {
        while (length > 0)
        {
            var segmentIndex = (int)(start / SegmentSize);
            var segmentOffset = (int)(start % SegmentSize);
            var segment = _segments[segmentIndex];
            var take = (int)Math.Min(length, segment.Length - segmentOffset);
            segment.AsSpan(segmentOffset, take).Clear();
            start += take;
            length -= take;
        }
    }

    private void Spill()
    {
        var file = CreateFile();
        try
        {
            var remaining = _length;
            foreach (var segment in _segments)
            {
                var count = (int)Math.Min(remaining, segment.Length);
                file.Write(segment.AsSpan(0, count));
                remaining -= count;
                if (remaining == 0) { break; }
            }
            FinishSpill(file);
        }
        finally
        {
            if (_file != file) { file.Dispose(); }
        }
    }

    private async ValueTask SpillAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = CreateFile();
        try
        {
            var remaining = _length;
            foreach (var segment in _segments)
            {
                var count = (int)Math.Min(remaining, segment.Length);
                await file.WriteAsync(segment.AsMemory(0, count), cancellationToken);
                remaining -= count;
                if (remaining == 0) { break; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            FinishSpill(file);
        }
        finally
        {
            if (_file != file) { await file.DisposeAsync(); }
        }
    }

    private FileStream CreateFile()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read | FileShare.Delete,
            BufferSize = 1,
            Options = FileOptions.Asynchronous | FileOptions.RandomAccess | FileOptions.DeleteOnClose
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return new FileStream
        (
            Path.Combine(_temporaryDirectory, $"yabt-content-{Guid.NewGuid():N}.tmp"),
            options
        );
    }

    private void FinishSpill(FileStream file)
    {
        file.Position = _position;
        _file = file;
        ReleaseMemory();
    }

    private FileStream? ReleaseBackingIfUnused()
    {
        if (!_disposed || _readers != 0 || _backingReleased) { return null; }
        _backingReleased = true;
        ReleaseMemory();
        return _file;
    }

    private void ReleaseMemory()
    {
        _segments.Clear();
        _factory.Release(_capacity);
        _capacity = 0;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ThrowIfNotWritable()
    {
        ThrowIfDisposed();
        if (_sealed) { throw new NotSupportedException("Sealed temporary content is immutable."); }
    }
}

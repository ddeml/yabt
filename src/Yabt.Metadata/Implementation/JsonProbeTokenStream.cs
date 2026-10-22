using System.Text;
using System.Text.Json;

namespace Yabt.Metadata.Implementation;

/// <summary>
/// Validates string and number tokens incrementally while replacing their
/// irrelevant contents with bounded tokens. Structural validation remains owned
/// by Utf8JsonReader. Only strings short enough to match the requested document
/// type or its property name are retained; huge ordinary payloads remain streams.
/// </summary>
internal sealed class JsonProbeTokenStream : Stream
{
    private readonly StreamReader _reader;
    private readonly char[] _input = new char[4096];
    private readonly char[] _capture;
    private readonly string _overflowValue;
    private byte[] _output = [];
    private int _inputCount;
    private int _inputPosition;
    private int _outputPosition;
    private int _lookahead = -1;
    private bool _firstCharacter = true;
    private bool _complete;

    public JsonProbeTokenStream(Stream source, int maximumStringLength)
    {
        _reader = new(source, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _capture = new char[maximumStringLength];
        _overflowValue = new('#', maximumStringLength + 1);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync
    (
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        var written = 0;
        while (written < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_outputPosition == _output.Length)
            {
                if (_complete) { break; }
                _output = await ReadTokenAsync(cancellationToken);
                _outputPosition = 0;
                if (_complete) { break; }
            }
            var count = Math.Min(buffer.Length - written, _output.Length - _outputPosition);
            _output.AsMemory(_outputPosition, count).CopyTo(buffer[written..]);
            _outputPosition += count;
            written += count;
        }
        return written;
    }

    private async ValueTask<byte[]> ReadTokenAsync(CancellationToken cancellationToken)
    {
        var character = await ReadCharacterAsync(cancellationToken);
        if (_firstCharacter)
        {
            _firstCharacter = false;
            if (character == '\ufeff') { character = await ReadCharacterAsync(cancellationToken); }
        }
        while (character is ' ' or '\t' or '\r' or '\n')
        {
            character = await ReadCharacterAsync(cancellationToken);
        }
        switch (character)
        {
            case -1:
                _complete = true;
                return [];
            case '{' or '}' or '[' or ']' or ':' or ',':
                return [(byte)character];
            case '"':
                return await ReadStringAsync(cancellationToken);
            case 't':
                await ReadLiteralTailAsync("rue", cancellationToken);
                return "true"u8.ToArray();
            case 'f':
                await ReadLiteralTailAsync("alse", cancellationToken);
                return "false"u8.ToArray();
            case 'n':
                await ReadLiteralTailAsync("ull", cancellationToken);
                return "null"u8.ToArray();
            case '-' or >= '0' and <= '9':
                await ReadNumberAsync(character, cancellationToken);
                return "0"u8.ToArray();
            default:
                throw new JsonException("Invalid JSON token while probing document type.");
        }
    }

    private async ValueTask<byte[]> ReadStringAsync(CancellationToken cancellationToken)
    {
        var captured = 0;
        var overflow = false;
        while (true)
        {
            var character = await ReadCharacterAsync(cancellationToken);
            if (character == '"') { break; }
            if (character < 0x20) { throw new JsonException("Invalid or unterminated JSON string."); }
            if (character == '\\')
            {
                character = await ReadCharacterAsync(cancellationToken);
                character = character switch
                {
                    '"' or '\\' or '/' => character,
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'u' => await ReadUnicodeEscapeAsync(cancellationToken),
                    _ => throw new JsonException("Invalid JSON string escape.")
                };
            }
            if (captured < _capture.Length) { _capture[captured++] = (char)character; }
            else { overflow = true; }
        }
        return JsonSerializer.SerializeToUtf8Bytes(overflow ? _overflowValue : new string(_capture, 0, captured));
    }

    private async ValueTask<int> ReadUnicodeEscapeAsync(CancellationToken cancellationToken)
    {
        var value = 0;
        for (var index = 0; index < 4; index++)
        {
            var character = await ReadCharacterAsync(cancellationToken);
            var digit = character switch
            {
                >= '0' and <= '9' => character - '0',
                >= 'a' and <= 'f' => character - 'a' + 10,
                >= 'A' and <= 'F' => character - 'A' + 10,
                _ => throw new JsonException("Invalid JSON Unicode escape.")
            };
            value = value * 16 + digit;
        }
        return value;
    }

    private async ValueTask ReadLiteralTailAsync(string tail, CancellationToken cancellationToken)
    {
        foreach (var expected in tail)
        {
            if (await ReadCharacterAsync(cancellationToken) != expected)
            {
                throw new JsonException("Invalid JSON literal.");
            }
        }
    }

    private async ValueTask ReadNumberAsync(int character, CancellationToken cancellationToken)
    {
        if (character == '-') { character = await ReadCharacterAsync(cancellationToken); }
        if (character == '0')
        {
            character = await ReadCharacterAsync(cancellationToken);
            if (IsDigit(character)) { throw new JsonException("A JSON number cannot have a leading zero."); }
        }
        else
        {
            if (character is < '1' or > '9') { throw new JsonException("Invalid JSON number."); }
            do { character = await ReadCharacterAsync(cancellationToken); } while (IsDigit(character));
        }
        if (character == '.')
        {
            character = await ReadCharacterAsync(cancellationToken);
            if (!IsDigit(character)) { throw new JsonException("A JSON fraction requires digits."); }
            do { character = await ReadCharacterAsync(cancellationToken); } while (IsDigit(character));
        }
        if (character is 'e' or 'E')
        {
            character = await ReadCharacterAsync(cancellationToken);
            if (character is '+' or '-') { character = await ReadCharacterAsync(cancellationToken); }
            if (!IsDigit(character)) { throw new JsonException("A JSON exponent requires digits."); }
            do { character = await ReadCharacterAsync(cancellationToken); } while (IsDigit(character));
        }
        _lookahead = character;
    }

    private ValueTask<int> ReadCharacterAsync(CancellationToken cancellationToken)
    {
        if (_lookahead >= 0)
        {
            var character = _lookahead;
            _lookahead = -1;
            return ValueTask.FromResult(character);
        }
        if (_inputPosition < _inputCount) { return ValueTask.FromResult((int)_input[_inputPosition++]); }
        return RefillAsync(cancellationToken);
    }

    private async ValueTask<int> RefillAsync(CancellationToken cancellationToken)
    {
        _inputCount = await _reader.ReadAsync(_input, cancellationToken);
        _inputPosition = 0;
        return _inputCount == 0 ? -1 : _input[_inputPosition++];
    }

    private static bool IsDigit(int character) => character is >= '0' and <= '9';

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _reader.Dispose(); }
        base.Dispose(disposing);
    }
}

using System.IO.Hashing;

namespace Yabt.Core.Models;

/// <summary>
/// Defines the single hash algorithm used for hashes created by YABT. xxHash128 is a fast,
/// non-cryptographic change detector. YABT retains all 128 bits, using unpadded Base64URL in
/// metadata and lowercase unpadded Base32hex in portable file names.
/// </summary>
public static class ArchiveHash
{
    private const string Base32HexAlphabet = "0123456789abcdefghijklmnopqrstuv";

    public const string AlgorithmName = "xxh128";
    public const int ValueLengthInBytes = 16;
    public const int EncodedValueLength = 22;
    public const int FileNameEncodedValueLength = 26;

    public static string Compute(ReadOnlySpan<byte> content)
    {
        var hash = new XxHash128();
        hash.Append(content);
        return Format(hash.GetHashAndReset());
    }

    public static string Compute
    (
        Stream content,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(content);

        var hash = new XxHash128();
        var buffer = new byte[81_920];
        int bytesRead;
        while ((bytesRead = content.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.Append(buffer.AsSpan(0, bytesRead));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Format(hash.GetHashAndReset());
    }

    public static async Task<string> ComputeAsync
    (
        Stream content,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(content);

        var hash = new XxHash128();
        await hash.AppendAsync(content, cancellationToken);
        return Format(hash.GetHashAndReset());
    }

    public static string Format(ReadOnlySpan<byte> hash)
    {
        if (hash.Length != ValueLengthInBytes)
        {
            throw new ArgumentException
            (
                $"An xxHash128 value must contain exactly {ValueLengthInBytes} bytes.",
                nameof(hash)
            );
        }

        var encodedHash = Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return $"{AlgorithmName}:{encodedHash}";
    }

    public static bool IsValid(string? value)
    {
        var expectedPrefix = $"{AlgorithmName}:";
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            value.Length != expectedPrefix.Length + EncodedValueLength)
        {
            return false;
        }

        var encodedValue = value.AsSpan(expectedPrefix.Length);
        foreach (var character in encodedValue)
        {
            if ((character < 'A' || character > 'Z') &&
                (character < 'a' || character > 'z') &&
                (character < '0' || character > '9') &&
                character is not '-' and not '_')
            {
                return false;
            }
        }

        // A 16-byte value leaves only two data bits in the final Base64 character. Requiring one
        // of these four characters rejects noncanonical values with nonzero padding bits.
        return encodedValue[^1] is 'A' or 'Q' or 'g' or 'w';
    }

    /// <summary>
    /// Formats a hash as a lowercase, case-fold-stable token for portable file names. JSON uses
    /// the shorter Base64URL representation, while file names use Base32hex because common file
    /// systems compare names without regard to letter case.
    /// </summary>
    public static string FormatFileNameToken(ReadOnlySpan<byte> hash)
    {
        if (hash.Length != ValueLengthInBytes)
        {
            throw new ArgumentException(
                $"The {AlgorithmName} hash must contain {ValueLengthInBytes} bytes.",
                nameof(hash));
        }

        Span<char> encodedFileNameValue = stackalloc char[FileNameEncodedValueLength];
        var outputIndex = 0;
        var bitBuffer = 0;
        var bitCount = 0;

        foreach (var hashByte in hash)
        {
            bitBuffer = (bitBuffer << 8) | hashByte;
            bitCount += 8;

            while (bitCount >= 5)
            {
                bitCount -= 5;
                encodedFileNameValue[outputIndex++] =
                    Base32HexAlphabet[(bitBuffer >> bitCount) & 31];

                bitBuffer = bitCount == 0
                    ? 0
                    : bitBuffer & ((1 << bitCount) - 1);
            }
        }

        if (bitCount > 0)
        {
            encodedFileNameValue[outputIndex++] =
                Base32HexAlphabet[(bitBuffer << (5 - bitCount)) & 31];
        }

        if (outputIndex != FileNameEncodedValueLength)
        {
            throw new InvalidOperationException("The xxHash128 file-name token had an unexpected length.");
        }

        return $"{AlgorithmName}-{encodedFileNameValue}";
    }

    public static string FormatFileNameToken(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsValid(value))
        {
            throw new ArgumentException("The value must be a canonical YABT xxHash128 hash.", nameof(value));
        }

        var encodedValue = value.AsSpan(AlgorithmName.Length + 1);
        Span<char> paddedBase64 = stackalloc char[24];
        encodedValue.CopyTo(paddedBase64);
        for (var index = 0; index < encodedValue.Length; index++)
        {
            paddedBase64[index] = paddedBase64[index] switch
            {
                '-' => '+',
                '_' => '/',
                _ => paddedBase64[index],
            };
        }

        paddedBase64[^2] = '=';
        paddedBase64[^1] = '=';

        Span<byte> hash = stackalloc byte[ValueLengthInBytes];
        if (!Convert.TryFromBase64Chars(paddedBase64, hash, out var bytesWritten) ||
            bytesWritten != ValueLengthInBytes)
        {
            throw new ArgumentException("The value must be a canonical YABT xxHash128 hash.", nameof(value));
        }

        return FormatFileNameToken(hash);
    }
}

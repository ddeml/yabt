using System.IO.Hashing;

namespace Yabt.Core.Models;

/// <summary>
/// Defines the single hash algorithm used for hashes created by YABT. xxHash128 is a fast,
/// non-cryptographic change detector; YABT retains all 128 bits to minimize accidental collisions.
/// </summary>
public static class ArchiveHash
{
    public const string AlgorithmName = "xxh128";
    public const int ValueLengthInBytes = 16;

    public static string Compute(ReadOnlySpan<byte> content)
    {
        var hash = new XxHash128();
        hash.Append(content);
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

        return $"{AlgorithmName}:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public static bool IsValid(string? value)
    {
        var expectedPrefix = $"{AlgorithmName}:";
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            value.Length != expectedPrefix.Length + (ValueLengthInBytes * 2))
        {
            return false;
        }

        foreach (var character in value.AsSpan(expectedPrefix.Length))
        {
            if ((character < '0' || character > '9') &&
                (character < 'a' || character > 'f'))
            {
                return false;
            }
        }

        return true;
    }
}

namespace Yabt.Core.Models;

/// <summary>
/// An exact, canonical path within an object store. Native filesystem paths are not object paths.
/// </summary>
public readonly struct ObjectPath : IEquatable<ObjectPath>, IComparable<ObjectPath>
{
    private readonly string? _value;

    private ObjectPath(string value)
    {
        _value = value;
    }

    public static ObjectPath Root => default;

    public string Value => _value ?? string.Empty;

    public bool IsRoot => Value.Length == 0;

    public string Name => Value[(Value.LastIndexOf('/') + 1)..];

    public ObjectPath Parent
    {
        get
        {
            var separator = Value.LastIndexOf('/');
            return separator < 0 ? Root : new(Value[..separator]);
        }
    }

    public static ObjectPath Parse(string? value)
    {
        if (string.IsNullOrEmpty(value)) { return Root; }

        if (value.Contains('\\'))
        {
            throw new ArgumentException
            (
                $"Object path '{value}' must use canonical '/' separators and cannot contain a backslash.",
                nameof(value)
            );
        }

        if (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
        {
            throw new ArgumentException
            (
                $"Object path '{value}' must be relative and cannot contain a drive prefix.",
                nameof(value)
            );
        }

        foreach (var segment in value.Split('/'))
        {
            if (segment is "" or "." or ".." || segment.Contains('\0'))
            {
                throw new ArgumentException
                (
                    $"Object path '{value}' contains an invalid segment.",
                    nameof(value)
                );
            }
        }

        return new(value);
    }

    public ObjectPath Combine(ObjectPath relativePath)
    {
        if (IsRoot) { return relativePath; }
        return relativePath.IsRoot ? this : new($"{Value}/{relativePath.Value}");
    }

    public bool IsUnder(ObjectPath prefix) =>
        prefix.IsRoot || Equals(prefix) ||
        Value.Length > prefix.Value.Length &&
        Value[prefix.Value.Length] == '/' &&
        Value.StartsWith(prefix.Value, StringComparison.Ordinal);

    public bool Equals(ObjectPath other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ObjectPath other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(ObjectPath other) => StringComparer.Ordinal.Compare(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator ==(ObjectPath left, ObjectPath right) => left.Equals(right);

    public static bool operator !=(ObjectPath left, ObjectPath right) => !left.Equals(right);
}

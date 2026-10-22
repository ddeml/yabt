namespace Yabt.Tests;

public sealed record HttpTestResponse
(
    int StatusCode,
    byte[]? Body = null,
    IReadOnlyDictionary<string, string>? Headers = null
);

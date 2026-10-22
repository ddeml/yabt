namespace Yabt.Tests;

public sealed record HttpTestRequest
(
    string Method,
    string Target,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body
);

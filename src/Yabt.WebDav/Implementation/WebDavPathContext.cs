namespace Yabt.WebDav.Implementation;

internal sealed record WebDavPathContext
(
    Uri Endpoint,
    IReadOnlyList<string> EndpointSegments,
    IReadOnlyList<string> RootSegments
);

namespace Yabt.WebDav;

public sealed class WebDavObjectStoreOptions
{
    public Uri? Endpoint { get; init; }

    public string? RootPath { get; init; }

    public string? CredentialRef { get; init; }
}

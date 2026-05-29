namespace Yabt.WebDav;

public sealed class WebDavArchiveOptions
{
    public Uri? Endpoint { get; init; }

    public string? RootPath { get; init; }

    public string? CredentialRef { get; init; }
}

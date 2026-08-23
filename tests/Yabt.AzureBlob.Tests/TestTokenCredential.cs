using Azure.Core;

namespace Yabt.AzureBlob.Tests;

internal sealed class TestTokenCredential : TokenCredential
{
    public override AccessToken GetToken
    (
        TokenRequestContext requestContext,
        CancellationToken cancellationToken = default
    ) => new("test-token", DateTimeOffset.MaxValue);

    public override ValueTask<AccessToken> GetTokenAsync
    (
        TokenRequestContext requestContext,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult(new AccessToken("test-token", DateTimeOffset.MaxValue));
}

using Microsoft.Extensions.Options;
using Yabt.Common;

namespace Yabt.AzureBlob.Implementation;

internal sealed class AzureBlobObjectStoreOptionsSnapshot
(
    AzureBlobObjectStoreOptions options
) : IOptionsMonitor<AzureBlobObjectStoreOptions>
{
    private readonly AzureBlobObjectStoreOptions _options = Check.NotNull(options);

    public AzureBlobObjectStoreOptions CurrentValue => _options;

    public AzureBlobObjectStoreOptions Get(string? name) => _options;

    public IDisposable? OnChange
    (
        Action<AzureBlobObjectStoreOptions, string?> listener
    ) => AzureBlobObjectStoreOptionsChangeRegistration.Instance;

    private sealed class AzureBlobObjectStoreOptionsChangeRegistration : IDisposable
    {
        public static AzureBlobObjectStoreOptionsChangeRegistration Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

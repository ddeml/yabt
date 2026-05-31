using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Format.Zip;

namespace Yabt.Format.Zip.Tests;

[TestClass]
public sealed class ZipArchiveFormatProviderTests
{
    [TestMethod]
    public void ServiceRegistrationRegistersZipFormatProvider()
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();

        var providers = serviceProvider.GetServices<IArchiveFormatProvider>().ToArray();

        Assert.AreEqual(1, providers.Length);
        var provider = providers[0];
        Assert.AreEqual(ZipArchiveFormatName.Value, provider.FormatName);
    }

    [TestMethod]
    [DataRow(nameof(IArchiveFormatProvider.BackupAsync))]
    [DataRow(nameof(IArchiveFormatProvider.RestoreAsync))]
    [DataRow(nameof(IArchiveFormatProvider.VerifyAsync))]
    public async Task OperationsRemainExplicitlyScaffolded(string operationName)
    {
        using var serviceProvider = CreateServices().BuildServiceProvider();
        var providers = serviceProvider.GetServices<IArchiveFormatProvider>().ToArray();
        Assert.AreEqual(1, providers.Length);
        var provider = providers[0];

        var exception = await ThrowsAsync<NotImplementedException>
        (
            () => InvokeOperationAsync(provider, operationName)
        );

        StringAssert.Contains(exception.Message, "scaffolded but not implemented");
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddYabtZipFormatProvider();

        return services;
    }

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        Assert.Fail($"Expected exception of type {typeof(TException).Name}.");
        throw new UnreachableException();
    }

    private static async Task InvokeOperationAsync
    (
        IArchiveFormatProvider provider,
        string operationName
    )
    {
        switch (operationName)
        {
            case nameof(IArchiveFormatProvider.BackupAsync):
                await provider.BackupAsync(CreateBackupRequest());
                break;

            case nameof(IArchiveFormatProvider.RestoreAsync):
                await provider.RestoreAsync(CreateRestoreRequest());
                break;

            case nameof(IArchiveFormatProvider.VerifyAsync):
                await provider.VerifyAsync(CreateVerifyRequest());
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(operationName), operationName, null);
        }
    }

    private static ArchiveFormatBackupRequest CreateBackupRequest()
    {
        return new
        (
            CreateRoot("source-archive", "source"),
            CreateRoot("target-archive", "target"),
            new FolderPolicy(ZipArchiveFormatName.Value)
        );
    }

    private static ArchiveFormatRestoreRequest CreateRestoreRequest()
    {
        return new
        (
            CreateRoot("source-archive", "source"),
            CreateRoot("target-archive", "target"),
            new FolderPolicy(ZipArchiveFormatName.Value)
        );
    }

    private static ArchiveFormatVerifyRequest CreateVerifyRequest()
    {
        return new
        (
            CreateRoot("source-archive", "source"),
            CreateRoot("target-archive", "target"),
            new FolderPolicy(ZipArchiveFormatName.Value)
        );
    }

    private static BackupRootDescriptor CreateRoot(string archiveId, string rootRole)
    {
        return new
        (
            BackupRootDescriptor.ExpectedDocumentType,
            1,
            archiveId,
            new DateTimeOffset(2026, 5, 31, 0, 0, 0, TimeSpan.Zero),
            ArchiveLayout.Default,
            [
                new BackupRootStore("primary", "memory"),
            ],
            rootRole
        );
    }
}

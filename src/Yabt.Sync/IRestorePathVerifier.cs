namespace Yabt.Sync;

public interface IRestorePathVerifier
{
    Task<RestorePathVerificationResult> VerifyAsync
    (
        RestorePathVerificationRequest request,
        CancellationToken cancellationToken = default
    );
}

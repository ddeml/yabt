using Yabt.Common.Async;

namespace Yabt.Common.Tests;

[TestClass]
public sealed class YabtTaskTests
{
    [TestMethod]
    public async Task RunHandlesResultThatCompletesAfterCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        using var releaseOperation = new ManualResetEventSlim();
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandonedResultHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new object();

        var task = YabtTask.Run
        (
            () =>
            {
                operationStarted.SetResult();
#pragma warning disable MSTEST0049 // Flow TestContext.CancellationToken to async operations
                releaseOperation.Wait();
#pragma warning restore MSTEST0049 // Flow TestContext.CancellationToken to async operations
                return result;
            },
            abandonedResult =>
            {
                Assert.AreSame(result, abandonedResult);
                abandonedResultHandled.SetResult();
            },
            cancellationToken: cancellationSource.Token
        );

        try
        {
#pragma warning disable MSTEST0049 // Flow TestContext.CancellationToken to async operations
            await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
#pragma warning restore MSTEST0049 // Flow TestContext.CancellationToken to async operations
            await cancellationSource.CancelAsync();

            await Assert.ThrowsAsync<OperationCanceledException>
            (
#pragma warning disable MSTEST0049 // Flow TestContext.CancellationToken to async operations
                () => task.WaitAsync(TimeSpan.FromSeconds(5))
#pragma warning restore MSTEST0049 // Flow TestContext.CancellationToken to async operations
            );

            releaseOperation.Set();
#pragma warning disable MSTEST0049 // Flow TestContext.CancellationToken to async operations
            await abandonedResultHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));
#pragma warning restore MSTEST0049 // Flow TestContext.CancellationToken to async operations
        }
        finally
        {
            releaseOperation.Set();
        }
    }

    [TestMethod]
    public async Task RunCancelsWaitForUnwrappedAsyncTaskAndObservesLateFailure()
    {
        using var cancellationSource = new CancellationTokenSource();
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandonedExceptionHandled = new TaskCompletionSource<Exception>
        (
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var expectedException = new InvalidOperationException("Late failure");

        var task = YabtTask.Run
        (
            async () =>
            {
                operationStarted.SetResult();
                await releaseOperation.Task;
                throw expectedException;
            },
            abandonedExceptionHandled.SetResult,
            cancellationSource.Token
        );
        try
        {
#pragma warning disable MSTEST0049 // Flow TestContext.CancellationToken to async operations
            await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
#pragma warning restore MSTEST0049 // Flow TestContext.CancellationToken to async operations
            await cancellationSource.CancelAsync();

            await Assert.ThrowsAsync<OperationCanceledException>
            (
#pragma warning disable MSTEST0049 // Flow TestContext.CancellationToken to async operations
                () => task.WaitAsync(TimeSpan.FromSeconds(5))
#pragma warning restore MSTEST0049 // Flow TestContext.CancellationToken to async operations
            );

            releaseOperation.SetResult();
#pragma warning disable MSTEST0049 // Flow TestContext.CancellationToken to async operations
            var observedException = await abandonedExceptionHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));
#pragma warning restore MSTEST0049 // Flow TestContext.CancellationToken to async operations
            Assert.AreSame(expectedException, observedException);
        }
        finally
        {
            releaseOperation.TrySetResult();
        }
    }
}

namespace ExpandOpenAI.Internal;

internal static class AsyncCompatibilityExtensions
{
    public static Task<string> ReadAsStringAsyncCompat(this HttpContent content, CancellationToken cancellationToken)
    {
#if NETSTANDARD2_1
        return content.ReadAsStringAsync().WithCancellation(cancellationToken);
#else
        return content.ReadAsStringAsync(cancellationToken);
#endif
    }

    public static Task<Stream> ReadAsStreamAsyncCompat(this HttpContent content, CancellationToken cancellationToken)
    {
#if NETSTANDARD2_1
        return content.ReadAsStreamAsync().WithCancellation(cancellationToken);
#else
        return content.ReadAsStreamAsync(cancellationToken);
#endif
    }

    public static ValueTask<string?> ReadLineAsyncCompat(this StreamReader reader, CancellationToken cancellationToken)
    {
#if NETSTANDARD2_1
        return new ValueTask<string?>(reader.ReadLineAsync().WithCancellation(cancellationToken));
#else
        return reader.ReadLineAsync(cancellationToken);
#endif
    }

    private static async Task<T> WithCancellation<T>(this Task<T> task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled || task.IsCompleted)
        {
            return await task.ConfigureAwait(false);
        }

        var cancellationTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancellationTask);

        if (task != await Task.WhenAny(task, cancellationTask.Task).ConfigureAwait(false))
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return await task.ConfigureAwait(false);
    }
}

using System.IO;

namespace ExpandOpenAI.Internal;

internal static class HttpRetryPolicy
{
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        OpenAICompatibleHttpRetryOptions options,
        CancellationToken cancellationToken)
    {
        for (var retryAttempt = 0; ; retryAttempt++)
        {
            using var request = requestFactory();

            try
            {
                var response = await httpClient.SendAsync(
                    request,
                    completionOption,
                    cancellationToken).ConfigureAwait(false);

                if (retryAttempt >= options.MaxRetryAttempts || !IsTransientStatusCode(response))
                {
                    return response;
                }

                var delay = GetDelay(response, options, retryAttempt + 1);
                response.Dispose();
                await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransientException(exception))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (retryAttempt >= options.MaxRetryAttempts)
                {
                    throw;
                }

                await DelayAsync(
                    GetExponentialDelay(options, retryAttempt + 1),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientStatusCode(HttpResponseMessage response)
    {
        var statusCode = (int)response.StatusCode;
        return statusCode == 408 || statusCode == 429 || statusCode >= 500;
    }

    private static bool IsTransientException(Exception exception)
    {
        return exception is HttpRequestException
            || exception is IOException
            || exception is TimeoutException
            || exception is OperationCanceledException;
    }

    private static TimeSpan GetDelay(
        HttpResponseMessage response,
        OpenAICompatibleHttpRetryOptions options,
        int retryAttempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return CapDelay(delta, options.MaxDelay);
        }

        if (retryAfter?.Date is { } retryDate)
        {
            var dateDelay = retryDate - DateTimeOffset.UtcNow;
            return CapDelay(dateDelay < TimeSpan.Zero ? TimeSpan.Zero : dateDelay, options.MaxDelay);
        }

        return GetExponentialDelay(options, retryAttempt);
    }

    private static TimeSpan GetExponentialDelay(
        OpenAICompatibleHttpRetryOptions options,
        int retryAttempt)
    {
        if (options.InitialDelay == TimeSpan.Zero || options.MaxDelay == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var multiplier = Math.Pow(2, Math.Max(0, retryAttempt - 1));
        var ticks = Math.Min(options.MaxDelay.Ticks, options.InitialDelay.Ticks * multiplier);
        return TimeSpan.FromTicks((long)ticks);
    }

    private static TimeSpan CapDelay(TimeSpan delay, TimeSpan maxDelay)
    {
        if (delay <= TimeSpan.Zero || maxDelay == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return delay <= maxDelay ? delay : maxDelay;
    }

    private static Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        return Task.Delay(delay, cancellationToken);
    }
}

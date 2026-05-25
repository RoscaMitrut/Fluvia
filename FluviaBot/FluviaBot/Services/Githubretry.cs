using System.Net;
using Octokit;

namespace FluviaBot.Services;

/// <summary>
/// Retry/backoff wrapper for Octokit calls. GitHub applies primary and
/// secondary rate limits and occasionally returns transient 5xx errors;
/// without backoff a busy installation will start failing reviews.
///
/// Strategy:
///   - Primary rate limit (RateLimitExceededException): wait until the
///     limit's reset time, reported by GetRetryAfterTimeSpan(), capped.
///   - Abuse / secondary rate limit (AbuseException): honour RetryAfterSeconds
///     if present, otherwise back off a fixed amount.
///   - Transient 5xx / transport failures: exponential backoff.
///   - Everything else (404, 422, auth failures): not retried — a second
///     attempt won't succeed.
/// </summary>
public static class GitHubRetry
{
    private const int MaxAttempts = 4;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(2);

    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        ILogger logger,
        string operation,
        CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex, attempt, out var delay))
            {
                logger.LogWarning(ex,
                    "GitHub call '{Operation}' failed (attempt {Attempt}/{Max}); " +
                    "retrying in {Delay:0.#}s.",
                    operation, attempt, MaxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>
    /// Decides whether an exception is worth retrying, and how long to wait.
    /// </summary>
    private static bool IsTransient(Exception ex, int attempt, out TimeSpan delay)
    {
        // Exponential backoff baseline: 2s, 4s, 8s … capped at MaxDelay.
        var backoff = Clamp(TimeSpan.FromSeconds(BaseDelay.TotalSeconds * Math.Pow(2, attempt - 1)));
        delay = backoff;

        switch (ex)
        {
            // Primary rate limit — wait until the window resets.
            case RateLimitExceededException rle:
                var resetIn = rle.GetRetryAfterTimeSpan();
                delay = Clamp(resetIn > TimeSpan.Zero ? resetIn : backoff);
                return true;

            // Abuse / secondary rate limit — honour Retry-After when supplied.
            case AbuseException abuse:
                var retryAfter = abuse.RetryAfterSeconds;
                delay = Clamp(retryAfter.HasValue
                    ? TimeSpan.FromSeconds(retryAfter.Value)
                    : backoff);
                return true;

            // Transient server-side failures.
            case ApiException apiEx when IsTransientStatus(apiEx.StatusCode):
                return true;

            // Transport-level failures (DNS blips, connection resets, timeouts
            // that are NOT our own cancellation token firing).
            case HttpRequestException:
                return true;
            case TaskCanceledException tce when tce.InnerException is TimeoutException:
                return true;

            default:
                return false;
        }
    }

    private static bool IsTransientStatus(HttpStatusCode status) => status
        is HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

    private static TimeSpan Clamp(TimeSpan delay)
    {
        if (delay < BaseDelay) return BaseDelay;
        if (delay > MaxDelay) return MaxDelay;
        return delay;
    }
}
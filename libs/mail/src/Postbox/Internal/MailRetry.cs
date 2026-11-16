using Microsoft.Extensions.Logging;

namespace Postbox.Internal;

// Повторы при временных ошибках: пауза RetryDelayMilliseconds, дальше удваивается
internal static class MailRetry
{
    public static async Task<T> RunAsync<T>(
        Func<int, CancellationToken, Task<T>> attempt,
        MailServerOptions options,
        ILogger logger,
        string operation,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        for (var number = 1; ; number++)
        {
            MailException error;
            try
            {
                return await attempt(number, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (MailErrors.Classify(exception, cancellationToken) is { } classified)
            {
                classified.Attempts = number;
                if (!classified.IsTransient || number >= options.MaxAttempts)
                {
                    if (ReferenceEquals(classified, exception))
                        throw;
                    throw classified;
                }
                error = classified;
            }

            var wait = TimeSpan.FromMilliseconds(options.RetryDelayMilliseconds * Math.Pow(2, number - 1));
            logger.LogWarning(error,
                "{Operation}: попытка {Attempt} из {MaxAttempts} не удалась ({Kind}), повтор через {Delay} мс",
                operation, number, options.MaxAttempts, error.Kind, (int)wait.TotalMilliseconds);
            await delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }
}

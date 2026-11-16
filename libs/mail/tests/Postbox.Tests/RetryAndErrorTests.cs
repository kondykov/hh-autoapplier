using System.Net.Sockets;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging.Abstractions;
using Postbox.Internal;

namespace Postbox.Tests;

public class RetryAndErrorTests
{
    private static readonly SmtpOptions Options = new() { Host = "smtp.test", MaxAttempts = 3, RetryDelayMilliseconds = 100 };

    [Fact]
    public async Task Transient_error_is_retried_with_doubling_delay()
    {
        var delays = new List<TimeSpan>();
        var calls = 0;

        var result = await MailRetry.RunAsync((attempt, _) =>
        {
            calls++;
            if (attempt < 3)
                throw new SocketException((int)SocketError.ConnectionRefused);
            return Task.FromResult(attempt);
        }, Options, NullLogger.Instance, "тест", CancellationToken.None, (d, _) => { delays.Add(d); return Task.CompletedTask; });

        Assert.Equal(3, result);
        Assert.Equal(3, calls);
        Assert.Equal([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)], delays);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts_with_reason()
    {
        var error = await Assert.ThrowsAsync<MailException>(() => MailRetry.RunAsync<int>(
            (_, _) => throw new SocketException((int)SocketError.ConnectionRefused),
            Options, NullLogger.Instance, "тест", CancellationToken.None, (_, _) => Task.CompletedTask));

        Assert.Equal(MailErrorKind.Connection, error.Kind);
        Assert.True(error.IsTransient);
        Assert.Equal(3, error.Attempts);
        Assert.IsType<SocketException>(error.InnerException);
    }

    [Fact]
    public async Task Permanent_error_is_not_retried()
    {
        var calls = 0;
        var error = await Assert.ThrowsAsync<MailException>(() => MailRetry.RunAsync<int>((_, _) =>
        {
            calls++;
            throw new MailKit.Security.AuthenticationException("535 bad credentials");
        }, Options, NullLogger.Instance, "тест", CancellationToken.None, (_, _) => Task.CompletedTask));

        Assert.Equal(1, calls);
        Assert.Equal(MailErrorKind.Authentication, error.Kind);
        Assert.Equal(1, error.Attempts);
    }

    [Fact]
    public async Task Caller_cancellation_and_foreign_exceptions_pass_through()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => MailRetry.RunAsync<int>(
            (_, token) => throw new OperationCanceledException(token), Options, NullLogger.Instance, "тест", cts.Token));

        await Assert.ThrowsAsync<InvalidOperationException>(() => MailRetry.RunAsync<int>(
            (_, _) => throw new InvalidOperationException(), Options, NullLogger.Instance, "тест", CancellationToken.None));
    }

    [Theory]
    [InlineData(SmtpStatusCode.MailboxBusy, true)]
    [InlineData(SmtpStatusCode.InsufficientStorage, true)]
    [InlineData(SmtpStatusCode.MailboxUnavailable, false)]
    [InlineData(SmtpStatusCode.TransactionFailed, false)]
    public void Smtp_4xx_is_transient_5xx_is_not(SmtpStatusCode code, bool transient)
    {
        var error = MailErrors.Classify(new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, code, "nope"), CancellationToken.None)!;
        Assert.Equal(MailErrorKind.Rejected, error.Kind);
        Assert.Equal(transient, error.IsTransient);
    }

    [Fact]
    public void Timeout_without_caller_cancellation_is_transient_timeout()
    {
        var error = MailErrors.Classify(new OperationCanceledException(), CancellationToken.None)!;
        Assert.Equal(MailErrorKind.Timeout, error.Kind);
        Assert.True(error.IsTransient);

        var socket = MailErrors.Classify(new IOException("x", new SocketException((int)SocketError.TimedOut)), CancellationToken.None)!;
        Assert.Equal(MailErrorKind.Timeout, socket.Kind);
    }
}

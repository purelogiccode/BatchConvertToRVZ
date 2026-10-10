using System.Globalization;
using RVZStudio.services;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace RVZStudio.Tests.Services;

public class UiLogSinkTests
{
    private static LogEvent CreateEvent(LogEventLevel level, string message, Exception? exception = null)
    {
        var template = new MessageTemplateParser().Parse(message);
        return new LogEvent(DateTimeOffset.Now, level, exception, template, []);
    }

    [Fact]
    public void EmitRaisesMessageLoggedWithTimestampedMessage()
    {
        var received = new List<string>();
        EventHandler<UiLogSink.LogMessageEventArgs> handler = (_, e) => received.Add(e.Message);
        UiLogSink.MessageLogged += handler;

        try
        {
            var sink = new UiLogSink();
            sink.Emit(CreateEvent(LogEventLevel.Information, "hello world"));

            var line = Assert.Single(received);
            Assert.Contains("hello world", line);
            Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\.\d{3}\]", line);
        }
        finally
        {
            UiLogSink.MessageLogged -= handler;
        }
    }

    [Fact]
    public void EmitWithNoSubscribersDoesNotThrow()
    {
        var sink = new UiLogSink();

        var exception = Record.Exception(() => sink.Emit(CreateEvent(LogEventLevel.Error, "boom")));

        Assert.Null(exception);
    }

    [Fact]
    public void EmitRendersMessageProperties()
    {
        var received = new List<string>();
        EventHandler<UiLogSink.LogMessageEventArgs> handler = (_, e) => received.Add(e.Message);
        UiLogSink.MessageLogged += handler;

        try
        {
            var sink = new UiLogSink(CultureInfo.InvariantCulture);
            sink.Emit(CreateEvent(LogEventLevel.Information, "value = {Value}", null));

            var line = Assert.Single(received);
            Assert.Contains("value =", line);
        }
        finally
        {
            UiLogSink.MessageLogged -= handler;
        }
    }

    [Fact]
    public void LogMessageEventArgsStoresMessage()
    {
        var args = new UiLogSink.LogMessageEventArgs("test message");

        Assert.Equal("test message", args.Message);
    }

    [Fact]
    public void EmitForwardsEveryLevel()
    {
        var received = new List<string>();
        EventHandler<UiLogSink.LogMessageEventArgs> handler = (_, e) => received.Add(e.Message);
        UiLogSink.MessageLogged += handler;

        try
        {
            var sink = new UiLogSink();
            sink.Emit(CreateEvent(LogEventLevel.Verbose, "verbose"));
            sink.Emit(CreateEvent(LogEventLevel.Debug, "debug"));
            sink.Emit(CreateEvent(LogEventLevel.Information, "information"));
            sink.Emit(CreateEvent(LogEventLevel.Warning, "warning"));
            sink.Emit(CreateEvent(LogEventLevel.Error, "error"));
            sink.Emit(CreateEvent(LogEventLevel.Fatal, "fatal"));

            Assert.Equal(6, received.Count);
        }
        finally
        {
            UiLogSink.MessageLogged -= handler;
        }
    }
}

public class BugReportSinkTests
{
    private static LogEvent CreateEvent(LogEventLevel level, string message, Exception? exception = null)
    {
        var template = new MessageTemplateParser().Parse(message);
        return new LogEvent(DateTimeOffset.Now, level, exception, template, []);
    }

    [Fact]
    public void EmitBelowMinimumLevelDoesNotReport()
    {
        var reported = false;
        var sink = new BugReportSink((_, _) =>
        {
            reported = true;
            return Task.FromResult(true);
        }, LogEventLevel.Warning);

        sink.Emit(CreateEvent(LogEventLevel.Information, "info"));
        sink.Emit(CreateEvent(LogEventLevel.Debug, "debug"));

        Assert.False(reported);
    }

    [Fact]
    public async Task EmitWarningReportsMessageWithoutException()
    {
        var completion = new TaskCompletionSource<(string Message, Exception? Exception)>();
        var sink = new BugReportSink((message, exception) =>
        {
            completion.TrySetResult((message, exception));
            return Task.FromResult(true);
        });

        sink.Emit(CreateEvent(LogEventLevel.Warning, "warning message"));

        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("warning message", result.Message);
        Assert.Null(result.Exception);
    }

    [Fact]
    public async Task EmitErrorWithExceptionReportsException()
    {
        var exception = new InvalidOperationException("failure");
        var completion = new TaskCompletionSource<(string Message, Exception? Exception)>();
        var sink = new BugReportSink((message, ex) =>
        {
            completion.TrySetResult((message, ex));
            return Task.FromResult(true);
        });

        sink.Emit(CreateEvent(LogEventLevel.Error, "error message", exception));

        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("error message", result.Message);
        Assert.Same(exception, result.Exception);
    }

    [Fact]
    public async Task EmitRendersMessageTemplateProperties()
    {
        var completion = new TaskCompletionSource<string>();
        var sink = new BugReportSink((message, _) =>
        {
            completion.TrySetResult(message);
            return Task.FromResult(true);
        }, formatProvider: CultureInfo.InvariantCulture);

        var template = new MessageTemplateParser().Parse("File {FileName} failed");
        var property = new LogEventProperty("FileName", new ScalarValue("game.iso"));
        sink.Emit(new LogEvent(DateTimeOffset.Now, LogEventLevel.Warning, null, template, [property]));

        var message = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("File \"game.iso\" failed", message);
    }

    [Fact]
    public async Task EmitSwallowsReporterExceptions()
    {
        var called = new TaskCompletionSource<bool>();
        var sink = new BugReportSink((_, _) =>
        {
            called.TrySetResult(true);
            throw new InvalidOperationException("reporter failed");
        });

        sink.Emit(CreateEvent(LogEventLevel.Error, "error"));

        // The reporter delegate ran and its exception was swallowed by the sink.
        await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EmitWithCustomMinimumLevelForwardsOnlyAtOrAboveIt()
    {
        var reported = new List<string>();
        var completion = new TaskCompletionSource<bool>();
        var sink = new BugReportSink((message, _) =>
        {
            reported.Add(message);
            completion.TrySetResult(true);
            return Task.FromResult(true);
        }, LogEventLevel.Error);

        sink.Emit(CreateEvent(LogEventLevel.Warning, "warning"));
        sink.Emit(CreateEvent(LogEventLevel.Error, "error"));

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["error"], reported);
    }
}

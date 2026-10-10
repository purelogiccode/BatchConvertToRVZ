using Serilog.Core;
using Serilog.Events;

namespace RVZStudio.services;

/// <summary>
/// A Serilog sink that forwards log events at or above a minimum level (Warning by default)
/// to the BugReport API using the existing <see cref="BugReportService"/>.
/// Reporting is fire-and-forget and failures are swallowed so that logging never throws.
/// </summary>
public class BugReportSink : ILogEventSink
{
    private readonly Func<string, Exception?, Task<bool>> _reporter;
    private readonly IFormatProvider? _formatProvider;
    private readonly LogEventLevel _minimumLevel;

    /// <summary>
    /// Initializes a new instance of the <see cref="BugReportSink"/> class.
    /// </summary>
    /// <param name="bugReportService">The service used to deliver the reports.</param>
    /// <param name="minimumLevel">The minimum level to forward (Warning by default).</param>
    /// <param name="formatProvider">The format provider used to render messages.</param>
    public BugReportSink(
        BugReportService bugReportService,
        LogEventLevel minimumLevel = LogEventLevel.Warning,
        IFormatProvider? formatProvider = null)
        : this(
            (message, exception) => exception is null
                ? bugReportService.SendBugReportAsync(message)
                : bugReportService.SendBugReportAsync(message, exception),
            minimumLevel,
            formatProvider)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BugReportSink"/> class with a custom
    /// reporter delegate. Used for testing.
    /// </summary>
    /// <param name="reporter">The delegate that delivers a message (and optional exception).</param>
    /// <param name="minimumLevel">The minimum level to forward (Warning by default).</param>
    /// <param name="formatProvider">The format provider used to render messages.</param>
    internal BugReportSink(
        Func<string, Exception?, Task<bool>> reporter,
        LogEventLevel minimumLevel = LogEventLevel.Warning,
        IFormatProvider? formatProvider = null)
    {
        _reporter = reporter;
        _minimumLevel = minimumLevel;
        _formatProvider = formatProvider;
    }

    /// <summary>
    /// Forwards the log event to the Bug Report API when it meets the minimum level.
    /// Reporting is fire-and-forget and never throws.
    /// </summary>
    /// <param name="logEvent">The log event to forward.</param>
    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < _minimumLevel) return;

        var message = logEvent.RenderMessage(_formatProvider);
        var exception = logEvent.Exception;

        // Fire-and-forget: never block the logging pipeline and never let reporting failures surface.
        _ = Task.Run(async () =>
        {
            try
            {
                await _reporter(message, exception);
            }
            catch
            {
                // Silently ignore reporting failures.
            }
        });
    }
}

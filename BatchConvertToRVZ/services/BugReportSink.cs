using Serilog.Core;
using Serilog.Events;

namespace BatchConvertToRVZ.services;

/// <summary>
/// A Serilog sink that forwards log events at or above a minimum level (Warning by default)
/// to the BugReport API using the existing <see cref="BugReportService"/>.
/// Reporting is fire-and-forget and failures are swallowed so that logging never throws.
/// </summary>
public class BugReportSink : ILogEventSink
{
    private readonly BugReportService _bugReportService;
    private readonly IFormatProvider? _formatProvider;
    private readonly LogEventLevel _minimumLevel;

    public BugReportSink(
        BugReportService bugReportService,
        LogEventLevel minimumLevel = LogEventLevel.Warning,
        IFormatProvider? formatProvider = null)
    {
        _bugReportService = bugReportService;
        _minimumLevel = minimumLevel;
        _formatProvider = formatProvider;
    }

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
                if (exception != null)
                {
                    await _bugReportService.SendBugReportAsync(message, exception);
                }
                else
                {
                    await _bugReportService.SendBugReportAsync(message);
                }
            }
            catch
            {
                // Silently ignore reporting failures.
            }
        });
    }
}

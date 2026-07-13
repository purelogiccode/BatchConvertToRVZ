using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace BatchConvertToRVZ.services;

/// <summary>
/// Serilog configuration extensions for registering the application's custom sinks.
/// </summary>
public static class LoggingSinkExtensions
{
    extension(LoggerSinkConfiguration sinkConfiguration)
    {
        public LoggerConfiguration Ui(LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose,
            IFormatProvider? formatProvider = null)
        {
            return sinkConfiguration.Sink(new UiLogSink(formatProvider), restrictedToMinimumLevel);
        }

        public LoggerConfiguration BugReport(BugReportService bugReportService,
            LogEventLevel minimumLevel = LogEventLevel.Warning,
            IFormatProvider? formatProvider = null)
        {
            return sinkConfiguration.Sink(
                new BugReportSink(bugReportService, minimumLevel, formatProvider),
                minimumLevel);
        }
    }
}

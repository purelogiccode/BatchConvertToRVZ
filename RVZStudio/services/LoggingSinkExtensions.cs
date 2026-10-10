using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace RVZStudio.services;

/// <summary>
/// Serilog configuration extensions for registering the application's custom sinks.
/// </summary>
public static class LoggingSinkExtensions
{
    extension(LoggerSinkConfiguration sinkConfiguration)
    {
        /// <summary>
        /// Adds the UI sink that forwards rendered log lines to the on-screen log viewer.
        /// </summary>
        /// <param name="restrictedToMinimumLevel">The minimum level accepted by the sink.</param>
        /// <param name="formatProvider">The format provider used to render messages.</param>
        /// <returns>The logger configuration for chaining.</returns>
        public LoggerConfiguration Ui(LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose,
            IFormatProvider? formatProvider = null)
        {
            return sinkConfiguration.Sink(new UiLogSink(formatProvider), restrictedToMinimumLevel);
        }

        /// <summary>
        /// Adds the sink that forwards Warning and above to the Bug Report API.
        /// </summary>
        /// <param name="bugReportService">The service used to deliver the reports.</param>
        /// <param name="minimumLevel">The minimum level forwarded to the API.</param>
        /// <param name="formatProvider">The format provider used to render messages.</param>
        /// <returns>The logger configuration for chaining.</returns>
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

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using BatchConvertToRVZ.services;
using Serilog;
using Serilog.Events;

namespace BatchConvertToRVZ;

public partial class App
{
    // Bug Report API configuration
    private const string BugReportApiUrl = "https://www.purelogiccode.com/bugreport/api/send-bug-report";
    private const string BugReportApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    private const string ApplicationName = "BatchConvertToRVZ";

    // Stats API configuration
    private const string StatsApiUrl = "https://www.purelogiccode.com/ApplicationStats/stats";
    private const string StatsApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    private const string StatsApplicationId = "BatchConvertToRVZ";

    public static BugReportService? BugReportServiceInstance { get; private set; }
    public static StatsService? StatsServiceInstance { get; private set; }

    public App()
    {
        // Clean up old DLL files from previous versions
        CleanupOldDllFiles();

        // Initialize the services first (BugReportService needed by Serilog sink)
        BugReportServiceInstance = new BugReportService(BugReportApiUrl, BugReportApiKey, ApplicationName);
        StatsServiceInstance = new StatsService(StatsApiUrl, StatsApiKey, StatsApplicationId);

        // Bootstrap Serilog with all sinks BEFORE the UI starts so that every
        // message emitted during construction is captured.
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BatchConvertToRVZ",
            "logs",
            "log-.txt");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Ui(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.BugReport(BugReportServiceInstance, LogEventLevel.Warning, CultureInfo.InvariantCulture)
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024, // 10 MB
                rollOnFileSizeLimit: true,
                restrictedToMinimumLevel: LogEventLevel.Debug,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("BatchConvertToRVZ v{Version} starting", GetType().Assembly.GetName().Version?.ToString() ?? "0.0.0");

        // Set up global exception handling
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        // Register the Exit event handler
        Exit += App_Exit;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Send usage statistics on application launch
        if (StatsServiceInstance != null)
        {
            _ = Task.Run((Func<Task>)(async () =>
            {
                try
                {
                    await StatsServiceInstance.SendUsageStatsAsync();
                }
                catch (Exception ex)
                {
                    // Silently ignore rate limit exceptions - this is expected behavior
                    // when the user launches the app multiple times within the rate limit window
                    if (ex.Message.Contains("Rate Limit", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    // Only report other types of exceptions that might indicate actual problems
                    Log.Error(ex, "StatsService.OnStartup failed");
                }
            }));
        }
    }

    private static void CleanupOldDllFiles()
    {
        try
        {
            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var dllFilesToDelete = new[] { "7z_x64.dll", "7z_arm64.dll" };

            foreach (var dllFile in dllFilesToDelete)
            {
                var filePath = Path.Combine(appDirectory, dllFile);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }
        catch
        {
            // Silently ignore any errors during cleanup
        }
    }

    private void App_Exit(object sender, ExitEventArgs e)
    {
        // Dispose of the services
        BugReportServiceInstance?.Dispose();
        StatsServiceInstance?.Dispose();

        // Flush Serilog before exiting
        Log.CloseAndFlush();

        // Unregister event handlers to prevent memory leaks
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        DispatcherUnhandledException -= App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log.Fatal(exception, "AppDomain.UnhandledException");
            // Fatal: block briefly to try to send the report before the process exits
            TryReportFatal("AppDomain.UnhandledException", exception);
        }
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var ex = e.Exception;

        if (ex is IOException or TaskCanceledException or OperationCanceledException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Application.DispatcherUnhandledException (recoverable)");
            MessageBox.Show($"An unexpected but recoverable error occurred: {ex.Message}\n\nThe application will continue to run, but the current operation may have failed.",
                "Recoverable Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
        else
        {
            Log.Fatal(ex, "Application.DispatcherUnhandledException (fatal)");
            TryReportFatal("Application.DispatcherUnhandledException", ex);
            MessageBox.Show($"A fatal error occurred and the application must close: {ex.Message}\n\nA bug report has been sent.",
                "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "TaskScheduler.UnobservedTaskException");
        e.SetObserved();
    }

    /// <summary>
    /// Sends a bug report directly (bypassing the Serilog sink) so that even if the
    /// logging pipeline has already shut down the report is still delivered.
    /// For fatal exceptions this blocks with a 5‑second timeout.
    /// </summary>
    private static void TryReportFatal(string source, Exception exception, bool isFatal = true)
    {
        try
        {
            if (BugReportServiceInstance == null) return;

            var message = $"Error Source: {source}";
            var reportTask = BugReportServiceInstance.SendBugReportAsync(message, exception);

            if (isFatal)
            {
                reportTask.Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch
        {
            // Silently ignore any errors in the reporting process
        }
    }
}

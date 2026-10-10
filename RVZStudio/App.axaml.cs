using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RVZStudio.services;
using Serilog;
using Serilog.Events;

namespace RVZStudio;

/// <summary>
/// The RVZStudio Avalonia application. Configures Serilog, owns the Bug Report and Stats
/// services, wires global exception handlers and starts usage statistics on launch.
/// </summary>
public class App : Application
{
    // Bug Report API configuration
    private const string BugReportApiUrl = "https://www.purelogiccode.com/bugreport/api/send-bug-report";
    private const string BugReportApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    private const string ApplicationName = "RVZStudio";

    // Stats API configuration
    private const string StatsApiUrl = "https://www.purelogiccode.com/ApplicationStats/stats";
    private const string StatsApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    private const string StatsApplicationId = "RVZStudio";

    /// <summary>
    /// Gets the application-wide bug report service, or null before the application is initialized.
    /// </summary>
    public static BugReportService? BugReportServiceInstance { get; private set; }

    /// <summary>
    /// Gets the application-wide usage statistics service, or null before the application is initialized.
    /// </summary>
    public static StatsService? StatsServiceInstance { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="App"/> class, cleans up legacy files,
    /// creates the HTTP services and configures Serilog with the UI, file and bug report sinks.
    /// </summary>
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
            "RVZStudio",
            "logs",
            "log-.txt");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Ui(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.BugReport(BugReportServiceInstance, LogEventLevel.Warning, CultureInfo.InvariantCulture)
            .WriteTo.File(
                logPath,
                restrictedToMinimumLevel: LogEventLevel.Debug,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture, // 10 MB
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 14)
            .CreateLogger();

        Log.Information("RVZStudio v{Version} starting",
            GetType().Assembly.GetName().Version?.ToString() ?? "0.0.0");

        // Set up global exception handling
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        Dispatcher.UIThread.UnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    /// <summary>
    /// Loads the application XAML resources.
    /// </summary>
    public override void Initialize()
    {
        try
        {
            AvaloniaXamlLoader.Load(this);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to load application XAML");
            throw;
        }
    }

    /// <summary>
    /// Creates the main window, hooks the shutdown handler and fires the launch usage
    /// statistics request.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow();
                desktop.Exit += App_Exit;
            }

            base.OnFrameworkInitializationCompleted();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to initialize the main window");
            throw;
        }

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
        catch (Exception ex)
        {
            // Cleanup is best-effort only; log at Debug to avoid bug report noise.
            Log.Debug(ex, "Failed to clean up old DLL files");
        }
    }

    private static void App_Exit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        try
        {
            // Dispose of the services
            BugReportServiceInstance?.Dispose();
            StatsServiceInstance?.Dispose();

            // Dispose the shared HTTP handler that backs every service
            SharedHttpHandler.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error disposing services during shutdown");
        }

        // Flush Serilog before exiting
        Log.CloseAndFlush();

        // Unregister event handlers to prevent memory leaks
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        Dispatcher.UIThread.UnhandledException -= App_DispatcherUnhandledException;
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

    private static void App_DispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var ex = e.Exception;

        switch (ex)
        {
            case IOException or TaskCanceledException or OperationCanceledException or UnauthorizedAccessException:
                Log.Error(ex, "Application.DispatcherUnhandledException (recoverable)");
                _ = ShowMessageBoxSafelyAsync(
                    $"An unexpected but recoverable error occurred: {ex.Message}\n\nThe application will continue to run, but the current operation may have failed.",
                    "Recoverable Error");
                e.Handled = true;
                break;
            default:
                Log.Fatal(ex, "Application.DispatcherUnhandledException (fatal)");
                TryReportFatal("Application.DispatcherUnhandledException", ex);
                _ = ShowMessageBoxSafelyAsync(
                    $"A fatal error occurred and the application must close: {ex.Message}\n\nA bug report has been sent.",
                    "Fatal Error");
                break;
        }
    }

    private static async Task ShowMessageBoxSafelyAsync(string message, string title)
    {
        try
        {
            await dialogs.MessageBox.ShowAsync(null, message, title);
        }
        catch (Exception ex)
        {
            // Ignore failures while reporting an error, but keep a debug trace.
            Log.Debug(ex, "Failed to show message box while reporting an error");
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
        catch (Exception ex)
        {
            // Silently ignore any errors in the reporting process; the logging pipeline
            // may already be shut down, so only a debug trace is safe here.
            Log.Debug(ex, "Failed to send fatal bug report directly");
        }
    }
}

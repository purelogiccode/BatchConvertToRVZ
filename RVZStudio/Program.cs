using Avalonia;

namespace RVZStudio;

/// <summary>
/// Application entry point and Avalonia bootstrap configuration.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Starts the Avalonia desktop application.
    /// </summary>
    /// <param name="args">Command-line arguments passed to the application.</param>
    /// <remarks>
    /// Initialization code. Don't use any Avalonia, third-party APIs or any
    /// SynchronizationContext-reliant code before AppMain is called: things aren't initialized yet.
    /// </remarks>
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Serilog.Log.Fatal(ex, "Application terminated unexpectedly");
            Serilog.Log.CloseAndFlush();
            throw;
        }
    }

    /// <summary>
    /// Builds the Avalonia application builder. Also used by the visual designer.
    /// </summary>
    /// <returns>The configured <see cref="AppBuilder"/>.</returns>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

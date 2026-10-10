using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Serilog;

namespace RVZStudio.services;

/// <summary>
/// Captures screenshots of application windows and saves them as PNG files.
/// Screenshots are stored in the "Screenshot" folder next to the application; when that
/// folder cannot be written to (for example, a read-only installation folder), the
/// screenshot is saved to <c>%LOCALAPPDATA%\RVZStudio\Screenshot</c> instead.
/// </summary>
public class ScreenshotService
{
    private const string ApplicationFolderName = "RVZStudio";
    private const string ScreenshotFolderName = "Screenshot";

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScreenshotService"/> class.
    /// </summary>
    /// <param name="logger">The logger used to report screenshot activity and failures.</param>
    public ScreenshotService(ILogger logger)
    {
        _logger = logger.ForContext<ScreenshotService>();
    }

    /// <summary>
    /// Captures the given window to a timestamped PNG file. The file is first saved next to
    /// the application (in the "Screenshot" folder) and, if that fails, in the per-user
    /// application data folder.
    /// </summary>
    /// <param name="window">The window to capture.</param>
    /// <returns>The path of the saved screenshot, or null when the capture failed.</returns>
    public Task<string?> CaptureWindowAsync(Window window)
    {
        try
        {
            var scaling = window.RenderScaling;
            var width = (int)Math.Ceiling(window.ClientSize.Width * scaling);
            var height = (int)Math.Ceiling(window.ClientSize.Height * scaling);

            if (width <= 0 || height <= 0)
            {
                _logger.Information("{Message:l}",
                    "Error: Cannot take a screenshot because the window has no visible area.");
                return Task.FromResult<string?>(null);
            }

            using var renderBitmap = new RenderTargetBitmap(
                new PixelSize(width, height),
                new Vector(96 * scaling, 96 * scaling));

            renderBitmap.Render(window);

            // Milliseconds keep two screenshots taken within the same second from overwriting each other.
            var fileName = $"Screenshot_{DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)}.png";
            var filePath = SaveScreenshot(
                fileName,
                path => renderBitmap.Save(path, PngBitmapEncoderOptions.Default),
                GetPrimaryScreenshotFolder(),
                GetFallbackScreenshotFolder());

            if (filePath is null)
            {
                _logger.Error("Failed to save the screenshot to both the application folder and the fallback folder.");
                return Task.FromResult<string?>(null);
            }

            _logger.Information("{Message:l}", $"Screenshot saved: {filePath}");
            return Task.FromResult<string?>(filePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error taking screenshot: {Message}", ex.Message);
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Saves a screenshot to the primary folder and, when that fails, to the fallback folder.
    /// </summary>
    /// <param name="fileName">The screenshot file name.</param>
    /// <param name="save">The action that writes the image to the given full path.</param>
    /// <param name="primaryFolder">The preferred destination folder.</param>
    /// <param name="fallbackFolder">The folder used when the primary folder cannot be written.</param>
    /// <returns>The saved file path, or null when both locations failed.</returns>
    internal string? SaveScreenshot(string fileName, Action<string> save, string primaryFolder, string fallbackFolder)
    {
        return TrySaveToFolder(fileName, save, primaryFolder)
               ?? TrySaveToFolder(fileName, save, fallbackFolder);
    }

    /// <summary>
    /// Gets the preferred screenshot folder next to the application.
    /// </summary>
    /// <returns>The full path of the application's "Screenshot" folder.</returns>
    internal static string GetPrimaryScreenshotFolder()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ScreenshotFolderName);
    }

    /// <summary>
    /// Gets the per-user fallback screenshot folder
    /// (<c>%LOCALAPPDATA%\RVZStudio\Screenshot</c>).
    /// </summary>
    /// <returns>The full path of the fallback "Screenshot" folder.</returns>
    internal static string GetFallbackScreenshotFolder()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationFolderName,
            ScreenshotFolderName);
    }

    private string? TrySaveToFolder(string fileName, Action<string> save, string folder)
    {
        try
        {
            // Ensure the folder exists before writing the image.
            Directory.CreateDirectory(folder);

            var filePath = Path.Combine(folder, fileName);
            save(filePath);
            return filePath;
        }
        catch (Exception ex)
        {
            // A failure in the application folder (for example, a read-only Program Files
            // installation) is an expected environment condition: log at Information level
            // so the fallback is attempted without generating a bug report.
            _logger.Information("{Message:l}",
                $"Could not save screenshot to {folder}: {ex.Message}. Trying the next location.");
            return null;
        }
    }
}

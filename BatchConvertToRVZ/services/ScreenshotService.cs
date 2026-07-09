using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BatchConvertToRVZ.services;

/// <summary>
/// Service responsible for capturing a screenshot of a WPF window and saving it to disk.
/// </summary>
public class ScreenshotService
{
    private readonly Action<string> _logMessage;
    private readonly Func<string, Exception?, Task> _reportBugAsync;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScreenshotService"/> class.
    /// </summary>
    /// <param name="logMessage">Action to log messages.</param>
    /// <param name="reportBugAsync">Function to report bugs asynchronously with optional exception.</param>
    public ScreenshotService(
        Action<string> logMessage,
        Func<string, Exception?, Task> reportBugAsync)
    {
        _logMessage = logMessage;
        _reportBugAsync = reportBugAsync;
    }

    /// <summary>
    /// Captures the specified window and saves it as a PNG file inside the 'Screenshot' folder.
    /// The folder is created if it does not already exist.
    /// </summary>
    /// <param name="window">The window to capture.</param>
    /// <returns>The full path of the saved screenshot, or null if the capture failed.</returns>
    public async Task<string?> CaptureWindowAsync(Window window)
    {
        try
        {
            var width = (int)Math.Ceiling(window.ActualWidth);
            var height = (int)Math.Ceiling(window.ActualHeight);

            if (width <= 0 || height <= 0)
            {
                _logMessage("Error: Cannot take a screenshot because the window has no visible area.");
                return null;
            }

            var dpiScale = VisualTreeHelper.GetDpi(window);
            var renderBitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(width * dpiScale.DpiScaleX),
                (int)Math.Ceiling(height * dpiScale.DpiScaleY),
                dpiScale.PixelsPerInchX,
                dpiScale.PixelsPerInchY,
                PixelFormats.Pbgra32);

            renderBitmap.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

            var screenshotFolder = GetScreenshotFolder();
            Directory.CreateDirectory(screenshotFolder);

            var fileName = $"Screenshot_{DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.png";
            var filePath = Path.Combine(screenshotFolder, fileName);

            await using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(stream);
            }

            _logMessage($"Screenshot saved: {filePath}");
            return filePath;
        }
        catch (Exception ex)
        {
            _logMessage($"Error taking screenshot: {ex.Message}");
            await _reportBugAsync("Error taking screenshot", ex);
            return null;
        }
    }

    /// <summary>
    /// Gets the path to the 'Screenshot' folder located in the application directory.
    /// </summary>
    /// <returns>The full path to the 'Screenshot' folder.</returns>
    private static string GetScreenshotFolder()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Screenshot");
    }
}

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;

namespace BatchConvertToRVZ.services;

public class ScreenshotService
{
    private readonly ILogger _logger;

    public ScreenshotService(ILogger logger)
    {
        _logger = logger.ForContext<ScreenshotService>();
    }

    public async Task<string?> CaptureWindowAsync(Window window)
    {
        try
        {
            var width = (int)Math.Ceiling(window.ActualWidth);
            var height = (int)Math.Ceiling(window.ActualHeight);

            if (width <= 0 || height <= 0)
            {
                _logger.Information("{Message:l}", "Error: Cannot take a screenshot because the window has no visible area.");
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

            _logger.Information("{Message:l}", $"Screenshot saved: {filePath}");
            return filePath;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error taking screenshot: {Message}", ex.Message);
            return null;
        }
    }

    private static string GetScreenshotFolder()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Screenshot");
    }
}

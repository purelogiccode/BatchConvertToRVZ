using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Serilog;

namespace RVZStudio.services;

public class ScreenshotService
{
    private readonly ILogger _logger;

    public ScreenshotService(ILogger logger)
    {
        _logger = logger.ForContext<ScreenshotService>();
    }

    public Task<string?> CaptureWindowAsync(Window window)
    {
        try
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

                var screenshotFolder = GetScreenshotFolder();
                Directory.CreateDirectory(screenshotFolder);

                var fileName = $"Screenshot_{DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.png";
                var filePath = Path.Combine(screenshotFolder, fileName);

                renderBitmap.Save(filePath, PngBitmapEncoderOptions.Default);

                _logger.Information("{Message:l}", $"Screenshot saved: {filePath}");
                return Task.FromResult<string?>(filePath);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error taking screenshot: {Message}", ex.Message);
                return Task.FromResult<string?>(null);
            }
        }
        catch (Exception exception)
        {
            return Task.FromException<string?>(exception);
        }
    }

    private static string GetScreenshotFolder()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Screenshot");
    }
}

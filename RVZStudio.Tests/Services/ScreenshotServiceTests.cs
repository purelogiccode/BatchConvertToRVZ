using System.Globalization;
using RVZStudio.services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace RVZStudio.Tests.Services;

public class ScreenshotServiceTests : IDisposable
{
    private readonly List<string> _logMessages = [];
    private readonly string _tempDir;
    private readonly ScreenshotService _service;

    public ScreenshotServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RVZStudio_ScreenshotTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);

        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(new DelegatingSink(msg => _logMessages.Add(msg)))
            .CreateLogger();

        _service = new ScreenshotService(logger);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                /* ignore */
            }
        }

        GC.SuppressFinalize(this);
    }

    private sealed class DelegatingSink : ILogEventSink
    {
        private readonly Action<string> _onMessage;

        public DelegatingSink(Action<string> onMessage)
        {
            _onMessage = onMessage;
        }

        public void Emit(LogEvent logEvent)
        {
            var sw = new StringWriter();
            logEvent.MessageTemplate.Render(logEvent.Properties, sw, CultureInfo.InvariantCulture);
            _onMessage(sw.ToString());
        }
    }

    [Fact]
    public void GetPrimaryScreenshotFolderEndsWithScreenshotUnderBaseDirectory()
    {
        var folder = ScreenshotService.GetPrimaryScreenshotFolder();

        Assert.Equal(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Screenshot"), folder);
    }

    [Fact]
    public void GetFallbackScreenshotFolderIsUnderLocalApplicationData()
    {
        var folder = ScreenshotService.GetFallbackScreenshotFolder();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(localAppData, folder);
        Assert.EndsWith(Path.Combine("RVZStudio", "Screenshot"), folder);
    }

    [Fact]
    public void SaveScreenshotCreatesMissingFolderAndWritesFile()
    {
        var primaryFolder = Path.Combine(_tempDir, "primary");
        var fallbackFolder = Path.Combine(_tempDir, "fallback");

        var result = _service.SaveScreenshot("shot.png", path => File.WriteAllText(path, "image"), primaryFolder,
            fallbackFolder);

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(primaryFolder, "shot.png"), result);
        Assert.True(File.Exists(result));
        Assert.False(Directory.Exists(fallbackFolder));
    }

    [Fact]
    public void SaveScreenshotUsesFallbackWhenPrimaryFolderCannotBeCreated()
    {
        // Occupy the primary folder path with a file so CreateDirectory fails.
        var primaryFolder = Path.Combine(_tempDir, "blocked");
        File.WriteAllText(primaryFolder, "not a folder");
        var fallbackFolder = Path.Combine(_tempDir, "fallback");

        var result = _service.SaveScreenshot("shot.png", path => File.WriteAllText(path, "image"), primaryFolder,
            fallbackFolder);

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(fallbackFolder, "shot.png"), result);
        Assert.True(File.Exists(result));
        Assert.Contains(_logMessages, static m => m.Contains("Could not save screenshot"));
    }

    [Fact]
    public void SaveScreenshotUsesFallbackWhenPrimarySaveActionThrows()
    {
        var primaryFolder = Path.Combine(_tempDir, "primary");
        var fallbackFolder = Path.Combine(_tempDir, "fallback");

        var result = _service.SaveScreenshot(
            "shot.png",
            path =>
            {
                if (path.StartsWith(primaryFolder, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Disk full");
                }

                File.WriteAllText(path, "image");
            },
            primaryFolder,
            fallbackFolder);

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(fallbackFolder, "shot.png"), result);
        Assert.True(File.Exists(result));
        Assert.Contains(_logMessages, static m => m.Contains("Disk full"));
    }

    [Fact]
    public void SaveScreenshotReturnsNullWhenBothFoldersFail()
    {
        var primaryFolder = Path.Combine(_tempDir, "blocked");
        var fallbackFolder = Path.Combine(_tempDir, "also-blocked");
        File.WriteAllText(primaryFolder, "not a folder");
        File.WriteAllText(fallbackFolder, "not a folder");

        var result = _service.SaveScreenshot("shot.png", _ => { }, primaryFolder, fallbackFolder);

        Assert.Null(result);
    }

    [Fact]
    public void SaveScreenshotReturnsNullWhenSaveActionAlwaysThrows()
    {
        var primaryFolder = Path.Combine(_tempDir, "primary");
        var fallbackFolder = Path.Combine(_tempDir, "fallback");

        var result = _service.SaveScreenshot(
            "shot.png",
            _ => throw new UnauthorizedAccessException("Access denied"),
            primaryFolder,
            fallbackFolder);

        Assert.Null(result);
        Assert.Contains(_logMessages, static m => m.Contains("Access denied"));
    }

    [Fact]
    public void SaveScreenshotKeepsFileInPrimaryFolderWhenItSucceeds()
    {
        var primaryFolder = Path.Combine(_tempDir, "primary");
        var fallbackFolder = Path.Combine(_tempDir, "fallback");

        var result = _service.SaveScreenshot("a.png", path => File.WriteAllText(path, "x"), primaryFolder,
            fallbackFolder);

        Assert.Equal(Path.Combine(primaryFolder, "a.png"), result);
        Assert.False(Directory.Exists(fallbackFolder));
    }
}

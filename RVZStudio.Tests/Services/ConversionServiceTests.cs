using System.Globalization;
using System.IO.Compression;
using RVZStudio.services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace RVZStudio.Tests.Services;

public class ConversionServiceTests : IDisposable
{
    private readonly List<string> _logMessages = [];
    private readonly List<LogEventLevel> _logLevels = [];
    private readonly string _tempDir;

    public ConversionServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RVZStudio_Tests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
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

    private ConversionService CreateService()
    {
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(new DelegatingSink((level, msg) =>
            {
                _logLevels.Add(level);
                _logMessages.Add(msg);
            }))
            .CreateLogger();
        return new ConversionService(logger);
    }

    private sealed class DelegatingSink : ILogEventSink
    {
        private readonly Action<LogEventLevel, string> _onMessage;

        public DelegatingSink(Action<LogEventLevel, string> onMessage)
        {
            _onMessage = onMessage;
        }

        public void Emit(LogEvent logEvent)
        {
            var sw = new StringWriter();
            logEvent.MessageTemplate.Render(logEvent.Properties, sw, CultureInfo.InvariantCulture);
            _onMessage(logEvent.Level, sw.ToString());
        }
    }

    private string CreateTestZipArchive(string entryName, byte[]? entryContent = null)
    {
        var archivePath = Path.Combine(_tempDir, $"test_{Path.GetRandomFileName()}.zip");
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName);
        using var stream = entry.Open();
        stream.Write(entryContent ?? new byte[100]);
        return archivePath;
    }

    private string CreateCorrupt7ZipArchive()
    {
        var archivePath = Path.Combine(_tempDir, $"test_{Path.GetRandomFileName()}.7z");
        File.WriteAllBytes(archivePath, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x00, 0x00, 0x00]);
        return archivePath;
    }

    [Fact]
    public void Get7ZipExecutablePathReturnsPathEndingWith7ZipExe()
    {
        var result = ProcessHelper.Get7ZipExecutablePath();

        Assert.Contains(GetExpected7ZipExecutableName(), result);
    }

    private static string GetExpected7ZipExecutableName()
    {
        var suffix = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64
            ? "_arm64"
            : string.Empty;
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        return $"7za{suffix}{extension}";
    }

    [Fact]
    public void Get7ZipExecutablePathReturnsAbsolutePath()
    {
        var result = ProcessHelper.Get7ZipExecutablePath();

        Assert.True(Path.IsPathRooted(result));
    }

    [Fact]
    public void Get7ZipExecutablePathContainsBaseDirectory()
    {
        var result = ProcessHelper.Get7ZipExecutablePath();
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        Assert.StartsWith(baseDir, result);
    }

    [Fact]
    public async Task PerformBatchConversionAsyncEmptyFilesReturnsImmediately()
    {
        var service = CreateService();

        await service.PerformBatchConversionAsync(
            "dolphinTool", [], _tempDir, false, "zstd", 5, 131072, static (_, _, _) => { }, static _ => { },
            static _ => { }, CancellationToken.None);

        Assert.Contains("No files selected for conversion.", _logMessages);
    }

    [Fact]
    public async Task PerformBatchConversionAsyncCancellationThrowsOperationCanceled()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.PerformBatchConversionAsync(
                "dolphinTool", ["test.7z"], _tempDir, false, "zstd", 5, 131072, static (_, _, _) => { },
                static _ => { }, static _ => { }, cts.Token));
    }

    [Fact]
    public async Task PerformBatchConversionAsyncUnsupportedFileReturnsFailure()
    {
        var service = CreateService();
        var filePath = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(filePath, "not a disc image");

        var successCount = 0;
        var failureCount = 0;

        await service.PerformBatchConversionAsync(
            @"C:\nonexistent_path\fake_dolphin.exe", [filePath], _tempDir, false, "zstd", 5, 131072,
            static (_, _, _) => { }, _ => successCount++, _ => failureCount++, CancellationToken.None);

        Assert.Equal(0, successCount);
        Assert.Equal(1, failureCount);
    }

    [Fact]
    public async Task PerformBatchConversionAsyncCorrupt7ZLogsFallbackAttempt()
    {
        var service = CreateService();
        var archivePath = CreateCorrupt7ZipArchive();

        var failureCount = 0;

        await service.PerformBatchConversionAsync(
            "dolphinTool", [archivePath], _tempDir, false, "zstd", 5, 131072, static (_, _, _) => { }, static _ => { },
            _ => failureCount++, CancellationToken.None);

        Assert.Equal(1, failureCount);
        Assert.Contains(_logMessages, static m => m.Contains("falling back to 7za.exe"));
    }

    [Fact]
    public async Task PerformBatchConversionAsyncCorrupt7ZLogsCorruptMessageWhenBothFail()
    {
        var service = CreateService();
        var archivePath = CreateCorrupt7ZipArchive();

        await service.PerformBatchConversionAsync(
            "dolphinTool", [archivePath], _tempDir, false, "zstd", 5, 131072, static (_, _, _) => { }, static _ => { },
            static _ => { }, CancellationToken.None);

        Assert.Contains(_logMessages, static m => m.Contains("File may be corrupt"));
    }

    [Fact]
    public async Task PerformBatchConversionAsyncZipWithIsoConvertsSuccessfullyViaRvzSharp()
    {
        var service = CreateService();

        // Create a minimal ISO-like file with a valid GameCube disc header
        var isoContent = new byte[350_000];
        new Random(42).NextBytes(isoContent);
        isoContent[0x1C] = 0xC2; // GameCube disc magic 0xC2339F3D at offset 0x1C
        isoContent[0x1D] = 0x33;
        isoContent[0x1E] = 0x9F;
        isoContent[0x1F] = 0x3D;
        var archivePath = CreateTestZipArchive("game.iso", isoContent);

        var successCount = 0;
        var failureCount = 0;

        await service.PerformBatchConversionAsync(
            @"C:\nonexistent_path\fake_dolphin.exe", [archivePath], _tempDir, false, "zstd", 5, 131072,
            static (_, _, _) => { }, _ => successCount++, _ => failureCount++, CancellationToken.None);

        // Extraction should succeed and the conversion should be done natively by RVZSharp,
        // without needing DolphinTool
        Assert.Contains(_logMessages, static m => m.Contains("Extracted"));
        Assert.Contains(_logMessages, static m => m.Contains("Converted to RVZ using RVZSharp"));
        Assert.Equal(1, successCount);
        Assert.Equal(0, failureCount);
    }

    [Fact]
    public async Task PerformBatchConversionAsyncTooSmallIsoFallsBackToDolphinTool()
    {
        var service = CreateService();

        // A 100-byte ISO is too small for RVZSharp to encode, so it must fall back to DolphinTool
        var isoContent = new byte[100];
        new Random(42).NextBytes(isoContent);
        var archivePath = CreateTestZipArchive("game.iso", isoContent);

        var successCount = 0;
        var failureCount = 0;

        await service.PerformBatchConversionAsync(
            @"C:\nonexistent_path\fake_dolphin.exe", [archivePath], _tempDir, false, "zstd", 5, 131072,
            static (_, _, _) => { }, _ => successCount++, _ => failureCount++, CancellationToken.None);

        Assert.Contains(_logMessages, static m => m.Contains("Falling back to DolphinTool"));
        Assert.Equal(0, successCount);
        Assert.Equal(1, failureCount);

        // A missing optional DolphinTool is an expected environment condition and must not be
        // logged at Warning or Error, because those levels are forwarded to the Bug Report API
        // (regression: bug report 67998 "Missing critical files: DolphinTool.exe").
        Assert.Contains(_logMessages, static m => m.Contains("fallback engine is unavailable"));
        Assert.DoesNotContain(_logLevels, static level => level >= LogEventLevel.Warning);
    }

    [Fact]
    public async Task PerformBatchConversionAsyncZipWithRvzCopiesDirectly()
    {
        var service = CreateService();

        var rvzContent = new byte[100];
        new Random(42).NextBytes(rvzContent);
        var archivePath = CreateTestZipArchive("game.rvz", rvzContent);

        var successCount = 0;

        await service.PerformBatchConversionAsync(
            @"C:\nonexistent_path\fake_dolphin.exe", [archivePath], _tempDir, false, "zstd", 5, 131072,
            static (_, _, _) => { }, _ => successCount++, static _ => { }, CancellationToken.None);

        Assert.Equal(1, successCount);
        Assert.Contains(_logMessages, static m => m.Contains("Found RVZ file inside archive"));
    }

    [Fact]
    public async Task PerformBatchConversionAsyncZipWithNoSupportedFileReportsError()
    {
        var service = CreateService();
        var archivePath = CreateTestZipArchive("readme.txt", "hello world"u8.ToArray());

        var successCount = 0;
        var failureCount = 0;

        await service.PerformBatchConversionAsync(
            "dolphinTool", [archivePath], _tempDir, false, "zstd", 5, 131072, static (_, _, _) => { },
            _ => successCount++, _ => failureCount++, CancellationToken.None);

        Assert.Equal(0, successCount);
        Assert.Equal(1, failureCount);
        Assert.Contains(_logMessages, static m => m.Contains("No supported disc image found"));
    }

    [Fact]
    public async Task PerformBatchConversionAsyncMultipleFilesProcessesSequentially()
    {
        var service = CreateService();
        var processedFiles = new List<string>();

        var archive1 = CreateTestZipArchive("game1.iso", new byte[100]);
        var archive2 = CreateTestZipArchive("game2.iso", new byte[100]);

        await service.PerformBatchConversionAsync(
            @"C:\nonexistent_path\fake_dolphin.exe", [archive1, archive2], _tempDir, false, "zstd", 5, 131072,
            (_, _, name) => processedFiles.Add(name), static _ => { }, static _ => { }, CancellationToken.None);

        Assert.Equal(2, processedFiles.Count);
    }

    [Fact]
    public async Task PerformBatchConversionAsyncLogsProcessingMessages()
    {
        var service = CreateService();
        var archivePath = CreateTestZipArchive("game.iso", new byte[100]);

        await service.PerformBatchConversionAsync(
            @"C:\nonexistent_path\fake_dolphin.exe", [archivePath], _tempDir, false, "zstd", 5, 131072,
            static (_, _, _) => { }, static _ => { }, static _ => { }, CancellationToken.None);

        Assert.Contains(_logMessages, static m => m.Contains("Preparing for batch conversion"));
        Assert.Contains(_logMessages, static m => m.Contains("Processing:"));
    }

    [Fact]
    public async Task PerformBatchConversionAsyncDirectRvzCopiesWithoutDeleting()
    {
        var service = CreateService();
        var inputPath = Path.Combine(_tempDir, "game.rvz");
        var outputFolder = Path.Combine(_tempDir, "output");
        File.WriteAllBytes(inputPath, [1, 2, 3, 4, 5]);

        var successCount = 0;

        await service.PerformBatchConversionAsync(
            @"C:\nonexistent_path\fake_dolphin.exe", [inputPath], outputFolder, false, "zstd", 5, 131072,
            static (_, _, _) => { }, _ => successCount++, static _ => { }, CancellationToken.None);

        Assert.Equal(1, successCount);
        Assert.True(File.Exists(inputPath));
        Assert.True(File.Exists(Path.Combine(outputFolder, "game.rvz")));
        Assert.Contains(_logMessages, static m => m.Contains("already in RVZ format, copying"));
    }

    [Fact]
    public async Task PerformBatchConversionAsyncDirectRvzDeletesOriginalWhenRequested()
    {
        var service = CreateService();
        var inputPath = Path.Combine(_tempDir, "game.rvz");
        var outputFolder = Path.Combine(_tempDir, "output");
        File.WriteAllBytes(inputPath, [1, 2, 3, 4, 5]);

        var successCount = 0;

        await service.PerformBatchConversionAsync(
            @"C:\nonexistent_path\fake_dolphin.exe", [inputPath], outputFolder, true, "zstd", 5, 131072,
            static (_, _, _) => { }, _ => successCount++, static _ => { }, CancellationToken.None);

        Assert.Equal(1, successCount);
        Assert.False(File.Exists(inputPath));
        Assert.True(File.Exists(Path.Combine(outputFolder, "game.rvz")));
        Assert.Contains(_logMessages, static m => m.Contains("Deleted original file"));
    }

    [Fact]
    public async Task PerformBatchConversionAsyncCreatesOutputFolder()
    {
        var service = CreateService();
        var inputPath = Path.Combine(_tempDir, "game.rvz");
        File.WriteAllBytes(inputPath, [1, 2, 3]);
        var outputFolder = Path.Combine(_tempDir, "nested", "output");

        await service.PerformBatchConversionAsync(
            @"C:\nonexistent_path\fake_dolphin.exe", [inputPath], outputFolder, false, "zstd", 5, 131072,
            static (_, _, _) => { }, static _ => { }, static _ => { }, CancellationToken.None);

        Assert.True(Directory.Exists(outputFolder));
        Assert.True(File.Exists(Path.Combine(outputFolder, "game.rvz")));
    }

    [Fact]
    public async Task PerformBatchConversionAsyncCreatesOutputFolderForNativeEngine()
    {
        var service = CreateService();
        var isoPath = Path.Combine(_tempDir, "game.iso");
        var content = new byte[350_000];
        new Random(42).NextBytes(content);
        content[0x1C] = 0xC2;
        content[0x1D] = 0x33;
        content[0x1E] = 0x9F;
        content[0x1F] = 0x3D;
        File.WriteAllBytes(isoPath, content);

        // The output folder does not exist yet: the service must create it before encoding.
        var outputFolder = Path.Combine(_tempDir, "nested", "native-output");

        var successCount = 0;

        await service.PerformBatchConversionAsync(
            @"C:\nonexistent_path\fake_dolphin.exe", [isoPath], outputFolder, false, "zstd", 5, 131072,
            static (_, _, _) => { }, _ => successCount++, static _ => { }, CancellationToken.None);

        Assert.Equal(1, successCount);
        Assert.True(File.Exists(Path.Combine(outputFolder, "game.rvz")));
        Assert.Contains(_logMessages, static m => m.Contains("Converted to RVZ using RVZSharp"));
    }

    [Fact]
    public async Task PerformBatchConversionAsyncProgressCallbackReceivesTotalAndFileName()
    {
        var service = CreateService();
        var inputPath = Path.Combine(_tempDir, "game.rvz");
        File.WriteAllBytes(inputPath, [1, 2, 3]);
        var progress = new List<(int Processed, int Total, string Name)>();

        await service.PerformBatchConversionAsync(
            @"C:\nonexistent_path\fake_dolphin.exe", [inputPath], _tempDir, false, "zstd", 5, 131072,
            (processed, total, name) => progress.Add((processed, total, name)), static _ => { }, static _ => { },
            CancellationToken.None);

        var entry = Assert.Single(progress);
        Assert.Equal(1, entry.Processed);
        Assert.Equal(1, entry.Total);
        Assert.Equal(inputPath, entry.Name);
    }

    [Fact]
    public async Task PerformBatchConversionAsyncStripsCompoundExtensionForOutputName()
    {
        var service = CreateService();
        var inputPath = Path.Combine(_tempDir, "game.nkit.iso");
        File.WriteAllBytes(inputPath, new byte[100]);

        await service.PerformBatchConversionAsync(
            @"C:\nonexistent_path\fake_dolphin.exe", [inputPath], _tempDir, false, "zstd", 5, 131072,
            static (_, _, _) => { }, static _ => { }, static _ => { }, CancellationToken.None);

        Assert.Contains(_logMessages, static m => m.Contains("game.nkit.iso -> game.rvz"));
    }
}

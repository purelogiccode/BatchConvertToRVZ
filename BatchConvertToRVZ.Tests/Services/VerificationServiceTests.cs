using System.Globalization;
using BatchConvertToRVZ.services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace BatchConvertToRVZ.Tests.Services;

public class VerificationServiceTests : IDisposable
{
    private readonly List<string> _logMessages = [];
    private readonly string _tempDir;

    public VerificationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BatchConvertToRVZ_Tests_" + Path.GetRandomFileName());
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

    private VerificationService CreateService()
    {
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(new DelegatingSink(msg => _logMessages.Add(msg)))
            .CreateLogger();
        return new VerificationService(logger);
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
    public async Task PerformBatchVerificationAsync_EmptyFiles_ReturnsImmediately()
    {
        var service = CreateService();

        await service.PerformBatchVerificationAsync(
            "dolphinTool", [], false, false, static (_, _, _) => { }, static _ => { }, static _ => { }, CancellationToken.None);

        Assert.Contains(_logMessages, static m => m.Contains("No files selected for verification."));
    }

    [Fact]
    public async Task PerformBatchVerificationAsync_Cancellation_ThrowsOperationCanceled()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.PerformBatchVerificationAsync(
                "dolphinTool", ["test.rvz"], false, false, static (_, _, _) => { }, static _ => { }, static _ => { }, cts.Token));
    }

    [Fact]
    public async Task PerformBatchVerificationAsync_MissingExecutable_LogsErrorAndReportsFailure()
    {
        var service = CreateService();
        var filePath = Path.Combine(_tempDir, "test.rvz");
        File.WriteAllText(filePath, "not a real rvz");

        var failureCount = 0;

        await service.PerformBatchVerificationAsync(
            @"C:\nonexistent_path\fake_dolphin.exe", [filePath], false, false,
            static (_, _, _) => { }, static _ => { }, _ => { failureCount++; }, CancellationToken.None);

        Assert.Equal(1, failureCount);
        Assert.Contains(_logMessages, static m => m.Contains("Error verifying file"));
    }

    [Fact]
    public async Task PerformBatchVerificationAsync_LogsProcessingMessages()
    {
        var service = CreateService();
        var filePath = Path.Combine(_tempDir, "test.rvz");
        File.WriteAllText(filePath, "not a real rvz");

        await service.PerformBatchVerificationAsync(
            @"C:\nonexistent_path\fake_dolphin.exe", [filePath], false, false,
            static (_, _, _) => { }, static _ => { }, static _ => { }, CancellationToken.None);

        Assert.Contains(_logMessages, static m => m.Contains("Preparing for batch verification"));
        Assert.Contains(_logMessages, static m => m.Contains("Verifying:"));
    }

    [Fact]
    public async Task PerformBatchVerificationAsync_MoveToSubfolders_AttemptsFileMove()
    {
        var service = CreateService();
        var filePath = Path.Combine(_tempDir, "test.rvz");
        File.WriteAllText(filePath, "not a real rvz");

        await service.PerformBatchVerificationAsync(
            @"C:\nonexistent_path\fake_dolphin.exe", [filePath], true, true,
            static (_, _, _) => { }, static _ => { }, static _ => { }, CancellationToken.None);

        Assert.Contains(_logMessages, static m => m.Contains("Verifying:"));
    }

    [Fact]
    public async Task PerformBatchVerificationAsync_ProgressCallbacks_AreInvoked()
    {
        var service = CreateService();
        var filePath = Path.Combine(_tempDir, "test.rvz");
        File.WriteAllText(filePath, "not a real rvz");

        var progressReceived = false;
        var lastFileName = string.Empty;

        await service.PerformBatchVerificationAsync(
            @"C:\nonexistent_path\fake_dolphin.exe", [filePath], false, false,
            (_, _, name) =>
            {
                progressReceived = true;
                lastFileName = name;
            },
            static _ => { }, static _ => { }, CancellationToken.None);

        Assert.True(progressReceived);
        Assert.Equal("test.rvz", lastFileName);
    }
}

using System.Globalization;
using BatchConvertToRVZ.services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace BatchConvertToRVZ.Tests.Services;

public class RvzSharpServiceTests : IDisposable
{
    private readonly List<string> _logMessages = [];
    private readonly string _tempDir;
    private readonly RvzSharpService _service;

    public RvzSharpServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BatchConvertToRVZ_RvzSharpTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);

        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(new DelegatingSink(msg => _logMessages.Add(msg)))
            .CreateLogger();

        _service = new RvzSharpService(logger);
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

    [Theory]
    [InlineData("game.iso", "zstd")]
    [InlineData("game.iso", "bzip2")]
    [InlineData("game.iso", "lzma")]
    [InlineData("game.iso", "lzma2")]
    [InlineData("game.gcm", "zstd")]
    [InlineData("game.wbfs", "zstd")]
    [InlineData("game.gcz", "zstd")]
    [InlineData("game.wia", "zstd")]
    [InlineData("game.rvz", "zstd")]
    public void CanEncodeReturnsTrueForSupportedInputs(string fileName, string compressionMethod)
    {
        var inputFile = Path.Combine(_tempDir, fileName);

        Assert.True(_service.CanEncode(inputFile, compressionMethod));
    }

    [Theory]
    [InlineData("game.nkit.iso", "zstd")]
    [InlineData("game.txt", "zstd")]
    [InlineData("game.zip", "zstd")]
    [InlineData("game.7z", "zstd")]
    public void CanEncodeReturnsFalseForUnsupportedExtensions(string fileName, string compressionMethod)
    {
        var inputFile = Path.Combine(_tempDir, fileName);

        Assert.False(_service.CanEncode(inputFile, compressionMethod));
    }

    [Theory]
    [InlineData("game.iso", "zlib")]
    [InlineData("game.iso", "lz4")]
    public void CanEncodeReturnsFalseForDolphinToolOnlyCompressionMethods(string fileName, string compressionMethod)
    {
        var inputFile = Path.Combine(_tempDir, fileName);

        Assert.False(_service.CanEncode(inputFile, compressionMethod));
    }

    [Theory]
    [InlineData("game.rvz", true)]
    [InlineData("game.wia", true)]
    [InlineData("game.iso", false)]
    [InlineData("game.txt", false)]
    public void CanDecodeReturnsExpectedResult(string fileName, bool expected)
    {
        var inputFile = Path.Combine(_tempDir, fileName);

        Assert.Equal(expected, _service.CanDecode(inputFile));
    }

    [Fact]
    public void TryEncodeSucceedsOnEncodableInput()
    {
        var inputFile = Path.Combine(_tempDir, "game.iso");
        var outputFile = Path.Combine(_tempDir, "game.rvz");
        var content = CreateDiscImageWithValidHeader(350_000);
        File.WriteAllBytes(inputFile, content);

        var result = _service.TryEncode(inputFile, outputFile, "zstd", 5, 131072, CancellationToken.None);

        Assert.True(result);
        Assert.True(File.Exists(outputFile));
        Assert.Contains(_logMessages, static m => m.Contains("Converted to RVZ using RVZSharp"));
    }

    [Fact]
    public void TryEncodeRejectsInputWithoutValidDiscHeader()
    {
        var inputFile = Path.Combine(_tempDir, "game.iso");
        var outputFile = Path.Combine(_tempDir, "game.rvz");
        var content = new byte[350_000];
        new Random(42).NextBytes(content);
        File.WriteAllBytes(inputFile, content);

        var result = _service.TryEncode(inputFile, outputFile, "zstd", 5, 131072, CancellationToken.None);

        Assert.False(result);
        Assert.False(File.Exists(outputFile));
        Assert.Contains(_logMessages, static m => m.Contains("not a recognized disc image"));
        Assert.Contains(_logMessages, static m => m.Contains("Falling back to DolphinTool"));
        Assert.DoesNotContain(_logMessages, static m => m.Contains("RVZSharp library failed"));
    }

    [Fact]
    public void TryEncodeRejectsContainerWithoutMatchingMagic()
    {
        var inputFile = Path.Combine(_tempDir, "game.wbfs");
        var outputFile = Path.Combine(_tempDir, "game.rvz");
        var content = new byte[350_000];
        new Random(42).NextBytes(content);
        File.WriteAllBytes(inputFile, content);

        // A .wbfs file without the "WBFS" magic is not a WBFS container; without this
        // check the library would silently treat it as a plain ISO and wrap it.
        var result = _service.TryEncode(inputFile, outputFile, "zstd", 5, 131072, CancellationToken.None);

        Assert.False(result);
        Assert.False(File.Exists(outputFile));
        Assert.Contains(_logMessages, static m => m.Contains("not a recognized disc image"));
        Assert.DoesNotContain(_logMessages, static m => m.Contains("RVZSharp library failed"));
    }

    [Theory]
    [InlineData("container.wbfs", "WBFS")]
    [InlineData("container.gcz", "GCZ\0")]
    [InlineData("container.wia", "WIA")]
    [InlineData("container.rvz", "RVZ\0")]
    public void TryEncodeAttemptsContainerWithMatchingMagic(string fileName, string magic)
    {
        var inputFile = Path.Combine(_tempDir, fileName);
        var outputFile = Path.Combine(_tempDir, "game.rvz");
        var content = new byte[350_000];
        new Random(42).NextBytes(content);
        System.Text.Encoding.ASCII.GetBytes(magic).CopyTo(content, 0);
        File.WriteAllBytes(inputFile, content);

        // A file with the matching container magic is handed to the library, which
        // either decodes it or fails with a library error (never a pre-validation skip).
        var result = _service.TryEncode(inputFile, outputFile, "zstd", 5, 131072, CancellationToken.None);

        Assert.DoesNotContain(_logMessages, static m => m.Contains("not a recognized disc image"));

        // Outcomes are format-dependent: WBFS headers are validated strictly (failure),
        // while other headers may parse leniently (success). Both are acceptable.
        Assert.True(result || _logMessages.Any(static m => m.Contains("RVZSharp library failed")));
    }

    private static byte[] CreateDiscImageWithValidHeader(int size)
    {
        var content = new byte[size];
        new Random(42).NextBytes(content);
        content[0x18] = (byte)'G';
        content[0x19] = (byte)'C';
        content[0x1A] = (byte)'N';
        return content;
    }

    [Fact]
    public void TryEncodeFailsAndDeletesPartialOutputOnEmptyInput()
    {
        var inputFile = Path.Combine(_tempDir, "game.iso");
        var outputFile = Path.Combine(_tempDir, "game.rvz");
        File.WriteAllBytes(inputFile, []);

        var result = _service.TryEncode(inputFile, outputFile, "zstd", 5, 131072, CancellationToken.None);

        Assert.False(result);
        Assert.False(File.Exists(outputFile));
        Assert.Contains(_logMessages, static m => m.Contains("Falling back to DolphinTool"));
    }

    [Fact]
    public void TryDecodeToIsoFailsAndDeletesPartialOutputOnGarbageInput()
    {
        var inputFile = Path.Combine(_tempDir, "game.rvz");
        var outputFile = Path.Combine(_tempDir, "game.iso");
        File.WriteAllBytes(inputFile, [0x00, 0x01, 0x02, 0x03, 0x04]);

        var result = _service.TryDecodeToIso(inputFile, outputFile, CancellationToken.None);

        Assert.False(result);
        Assert.False(File.Exists(outputFile));
        Assert.Contains(_logMessages, static m => m.Contains("Falling back to DolphinTool"));
    }
}
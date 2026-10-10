using System.Globalization;
using RVZStudio.services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace RVZStudio.Tests.Services;

public class RvzSharpServiceTests : IDisposable
{
    private readonly List<string> _logMessages = [];
    private readonly string _tempDir;
    private readonly RvzSharpService _service;

    public RvzSharpServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RVZStudio_RvzSharpTests_" + Path.GetRandomFileName());
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

        Assert.True(RvzSharpService.CanEncode(inputFile, compressionMethod));
    }

    [Theory]
    [InlineData("game.nkit.iso", "zstd")]
    [InlineData("game.txt", "zstd")]
    [InlineData("game.zip", "zstd")]
    [InlineData("game.7z", "zstd")]
    public void CanEncodeReturnsFalseForUnsupportedExtensions(string fileName, string compressionMethod)
    {
        var inputFile = Path.Combine(_tempDir, fileName);

        Assert.False(RvzSharpService.CanEncode(inputFile, compressionMethod));
    }

    [Theory]
    [InlineData("game.iso", "zlib")]
    [InlineData("game.iso", "lz4")]
    public void CanEncodeReturnsFalseForDolphinToolOnlyCompressionMethods(string fileName, string compressionMethod)
    {
        var inputFile = Path.Combine(_tempDir, fileName);

        Assert.False(RvzSharpService.CanEncode(inputFile, compressionMethod));
    }

    [Theory]
    [InlineData("game.rvz", true)]
    [InlineData("game.wia", true)]
    [InlineData("game.iso", false)]
    [InlineData("game.txt", false)]
    public void CanDecodeReturnsExpectedResult(string fileName, bool expected)
    {
        var inputFile = Path.Combine(_tempDir, fileName);

        Assert.Equal(expected, RvzSharpService.CanDecode(inputFile));
    }

    [Fact]
    public void TryEncodeSucceedsOnEncodableInput()
    {
        var inputFile = Path.Combine(_tempDir, "game.iso");
        var outputFile = Path.Combine(_tempDir, "game.rvz");
        var content = CreateDiscImageWithValidHeader(350_000);
        File.WriteAllBytes(inputFile, content);

        var result = _service.TryEncode(inputFile, outputFile, "zstd", 5, 131072, scrub: false, progress: null, CancellationToken.None);

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

        var result = _service.TryEncode(inputFile, outputFile, "zstd", 5, 131072, scrub: false, progress: null, CancellationToken.None);

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
        // check the library would treat it as a plain ISO (and, since 1.0.1, reject it
        // at write time for lacking a disc header). Either way it must not be encoded.
        var result = _service.TryEncode(inputFile, outputFile, "zstd", 5, 131072, scrub: false, progress: null, CancellationToken.None);

        Assert.False(result);
        Assert.False(File.Exists(outputFile));
        Assert.Contains(_logMessages, static m => m.Contains("not a recognized disc image"));
        Assert.DoesNotContain(_logMessages, static m => m.Contains("RVZSharp library failed"));
    }

    [Theory]
    [InlineData("container.wbfs", "WBFS")]
    [InlineData("container.gcz", "\u0001\u00c0\v\xB1")]
    [InlineData("container.wia", "WIA\x01")]
    [InlineData("container.rvz", "RVZ\x01")]
    public void TryEncodeAttemptsContainerWithMatchingMagic(string fileName, string magic)
    {
        var inputFile = Path.Combine(_tempDir, fileName);
        var outputFile = Path.Combine(_tempDir, "game.rvz");
        var content = new byte[350_000];
        new Random(42).NextBytes(content);
        // Latin-1 maps every char 0x00-0xFF straight to its byte (ASCII would turn 0xC0
        // into '?'), so the GCZ magic 0x01 0xC0 0x0B 0xB1 and the RVZ/WIA "\x01" magics
        // land in the file byte-for-byte.
        System.Text.Encoding.Latin1.GetBytes(magic).CopyTo(content, 0);
        File.WriteAllBytes(inputFile, content);

        // A file with the matching container magic is handed to the library, which
        // either decodes it or fails with a library error (never a pre-validation skip).
        var result = _service.TryEncode(inputFile, outputFile, "zstd", 5, 131072, scrub: false, progress: null, CancellationToken.None);

        Assert.DoesNotContain(_logMessages, static m => m.Contains("not a recognized disc image"));

        // Outcomes are format-dependent: WBFS headers are validated strictly (failure),
        // while other headers may parse leniently (success). Both are acceptable.
        Assert.True(result || _logMessages.Any(static m =>
            m.Contains("RVZSharp library failed") || m.Contains("RVZSharp could not encode")));
    }

    private static byte[] CreateDiscImageWithValidHeader(int size)
    {
        var content = new byte[size];
        new Random(42).NextBytes(content);
        content[0x1C] = 0xC2; // GameCube disc magic 0xC2339F3D at offset 0x1C
        content[0x1D] = 0x33;
        content[0x1E] = 0x9F;
        content[0x1F] = 0x3D;
        return content;
    }

    [Fact]
    public void TryEncodeFailsAndDeletesPartialOutputOnEmptyInput()
    {
        var inputFile = Path.Combine(_tempDir, "game.iso");
        var outputFile = Path.Combine(_tempDir, "game.rvz");
        File.WriteAllBytes(inputFile, []);

        var result = _service.TryEncode(inputFile, outputFile, "zstd", 5, 131072, scrub: false, progress: null, CancellationToken.None);

        Assert.False(result);
        Assert.False(File.Exists(outputFile));
        Assert.Contains(_logMessages, static m => m.Contains("Falling back to DolphinTool"));
    }

    [Fact]
    public void TryDecodeFailsAndDeletesPartialOutputOnGarbageInput()
    {
        var inputFile = Path.Combine(_tempDir, "game.rvz");
        var outputFile = Path.Combine(_tempDir, "game.iso");
        File.WriteAllBytes(inputFile, [0x00, 0x01, 0x02, 0x03, 0x04]);

        var result = _service.TryDecode(inputFile, outputFile, "iso", scrub: false, progress: null,
            CancellationToken.None);

        Assert.False(result);
        Assert.False(File.Exists(outputFile));
        Assert.Contains(_logMessages, static m => m.Contains("Falling back to DolphinTool"));
    }

    [Theory]
    [InlineData("game.ciso")]
    [InlineData("game.wbi")]
    [InlineData("game.tgc")]
    [InlineData("game.nfs")]
    public void CanEncodeReturnsTrueForNewLegacyInputs(string fileName)
    {
        var inputFile = Path.Combine(_tempDir, fileName);

        Assert.True(RvzSharpService.CanEncode(inputFile, "zstd"));
    }

    [Theory]
    [InlineData("iso")]
    [InlineData("wia")]
    [InlineData("gcz")]
    [InlineData("ciso")]
    public void TryDecodeWritesNativeFormatsForGameCubeDisc(string outputFormat)
    {
        var rvzFile = CreateRvzFromSyntheticDisc();
        var outputFile = Path.Combine(_tempDir, $"game.{outputFormat}");

        var result = _service.TryDecode(rvzFile, outputFile, outputFormat, scrub: false, progress: null,
            CancellationToken.None);

        Assert.True(result);
        Assert.True(File.Exists(outputFile));
        Assert.Contains(_logMessages,
            m => m.Contains($"Converted to {outputFormat.ToUpperInvariant()} using RVZSharp"));
    }

    [Fact]
    public void TryDecodeWbfsOnGameCubeDiscFailsGracefully()
    {
        var rvzFile = CreateRvzFromSyntheticDisc();
        var outputFile = Path.Combine(_tempDir, "game.wbfs");

        var result = _service.TryDecode(rvzFile, outputFile, "wbfs", scrub: false, progress: null,
            CancellationToken.None);

        Assert.False(result);
        Assert.False(File.Exists(outputFile));
        Assert.Contains(_logMessages, static m => m.Contains("Falling back to DolphinTool"));
    }

    [Fact]
    public void TryVerifyReturnsTrueForValidDiscAndLogsHashes()
    {
        var rvzFile = CreateRvzFromSyntheticDisc();

        var result = _service.TryVerify(rvzFile, progress: null, CancellationToken.None);

        Assert.True(result);
        Assert.Contains(_logMessages, static m => m.Contains("Verified") && m.Contains("GameCube"));
        Assert.Contains(_logMessages, static m => m.Contains("Hashes for") && m.Contains("SHA1="));
    }

    [Fact]
    public void TryVerifyReturnsFalseForNonDiscImage()
    {
        var inputFile = Path.Combine(_tempDir, "game.rvz");
        File.WriteAllBytes(inputFile, [0x00, 0x01, 0x02, 0x03, 0x04]);

        var result = _service.TryVerify(inputFile, progress: null, CancellationToken.None);

        Assert.False(result);
        Assert.Contains(_logMessages, static m => m.Contains("not a GameCube/Wii disc image"));
    }

    [Fact]
    public void TryEncodeWithScrubSucceedsOnGameCubeDisc()
    {
        var inputFile = Path.Combine(_tempDir, "game.iso");
        var outputFile = Path.Combine(_tempDir, "game.rvz");
        File.WriteAllBytes(inputFile, CreateDiscImageWithValidHeader(350_000));

        var result = _service.TryEncode(inputFile, outputFile, "zstd", 5, 131072, scrub: true, progress: null,
            CancellationToken.None);

        Assert.True(result);
        Assert.True(File.Exists(outputFile));
    }

    private string CreateRvzFromSyntheticDisc()
    {
        var inputFile = Path.Combine(_tempDir, $"game_{Path.GetRandomFileName()}.iso");
        var outputFile = Path.Combine(_tempDir, $"game_{Path.GetRandomFileName()}.rvz");
        File.WriteAllBytes(inputFile, CreateDiscImageWithValidHeader(350_000));
        Assert.True(_service.TryEncode(inputFile, outputFile, "zstd", 5, 131072, scrub: false, progress: null,
            CancellationToken.None));
        return outputFile;
    }

    [Theory]
    [InlineData("game.iso", "ZSTD")]
    [InlineData("game.iso", "BZip2")]
    [InlineData("game.iso", "LZMA2")]
    public void CanEncodeIsCaseInsensitiveForCompressionMethod(string fileName, string compressionMethod)
    {
        var inputFile = Path.Combine(_tempDir, fileName);

        Assert.True(RvzSharpService.CanEncode(inputFile, compressionMethod));
    }

    [Theory]
    [InlineData("game.RVZ", true)]
    [InlineData("game.WIA", true)]
    [InlineData("game.Rvz", true)]
    [InlineData("game.ISO", false)]
    public void CanDecodeIsCaseInsensitiveForExtension(string fileName, bool expected)
    {
        var inputFile = Path.Combine(_tempDir, fileName);

        Assert.Equal(expected, RvzSharpService.CanDecode(inputFile));
    }

    [Fact]
    public void CanEncodeRejectsNkitGcz()
    {
        var inputFile = Path.Combine(_tempDir, "game.nkit.gcz");

        Assert.False(RvzSharpService.CanEncode(inputFile, "zstd"));
    }

    [Fact]
    public void CanEncodeRejectsUnknownCompressionMethod()
    {
        var inputFile = Path.Combine(_tempDir, "game.iso");

        Assert.False(RvzSharpService.CanEncode(inputFile, "unknown"));
    }

    [Fact]
    public void TryDecodeUnsupportedFormatReturnsFalseAndLogs()
    {
        var rvzFile = CreateRvzFromSyntheticDisc();
        var outputFile = Path.Combine(_tempDir, "game.xyz");

        var result = _service.TryDecode(rvzFile, outputFile, "xyz", scrub: false, progress: null,
            CancellationToken.None);

        Assert.False(result);
        Assert.Contains(_logMessages, static m => m.Contains("cannot write output format"));
    }

    [Fact]
    public void TryDecodeCancellationThrows()
    {
        var rvzFile = CreateRvzFromSyntheticDisc();
        var outputFile = Path.Combine(_tempDir, "cancelled.iso");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            _service.TryDecode(rvzFile, outputFile, "iso", scrub: false, progress: null, cts.Token));
    }

    [Fact]
    public void TryEncodeCancellationThrowsAndCleansPartialOutput()
    {
        var inputFile = Path.Combine(_tempDir, "game.iso");
        var outputFile = Path.Combine(_tempDir, "cancelled.rvz");
        File.WriteAllBytes(inputFile, CreateDiscImageWithValidHeader(350_000));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            _service.TryEncode(inputFile, outputFile, "zstd", 5, 131072, scrub: false, progress: null, cts.Token));
        Assert.False(File.Exists(outputFile));
    }

    [Fact]
    public void TryEncodeRejectsUnsupportedCompressionMethod()
    {
        var inputFile = Path.Combine(_tempDir, "game.iso");
        var outputFile = Path.Combine(_tempDir, "game.rvz");
        File.WriteAllBytes(inputFile, CreateDiscImageWithValidHeader(350_000));

        // The service must never silently substitute a different codec for the requested one.
        var result = _service.TryEncode(inputFile, outputFile, "zlib", 5, 131072, scrub: false, progress: null,
            CancellationToken.None);

        Assert.False(result);
        Assert.False(File.Exists(outputFile));
        Assert.Contains(_logMessages, static m => m.Contains("does not support compression method"));
    }
}

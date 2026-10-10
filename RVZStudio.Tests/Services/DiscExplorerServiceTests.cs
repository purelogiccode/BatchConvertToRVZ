using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using RVZStudio.Models;
using RVZStudio.services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace RVZStudio.Tests.Services;

public class DiscExplorerServiceTests : IDisposable
{
    private static readonly byte[] FileData = "RVZSTUDIO-TEST!!"u8.ToArray();

    private readonly List<string> _logMessages = [];
    private readonly string _tempDir;
    private readonly DiscExplorerService _service;

    public DiscExplorerServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RVZStudio_ExplorerTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);

        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(new DelegatingSink(msg => _logMessages.Add(msg)))
            .CreateLogger();

        _service = new DiscExplorerService(logger);
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

    private string WriteDisc(string fileName)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, CreateDiscWithFileSystem());
        return path;
    }

    /// <summary>
    /// Builds a synthetic GameCube disc with a valid FST: root/ -> sub/ -> file.bin
    /// (16 bytes at 0x2000).
    /// </summary>
    private static byte[] CreateDiscWithFileSystem()
    {
        const int fstOffset = 0x1000;
        const int fileOffset = 0x2000;
        const int fstSize = 50;

        var disc = new byte[0x3000];
        new Random(42).NextBytes(disc);
        disc[0x1C] = 0xC2; // GameCube disc magic 0xC2339F3D at offset 0x1C
        disc[0x1D] = 0x33;
        disc[0x1E] = 0x9F;
        disc[0x1F] = 0x3D;

        // Boot header: FST offset (0x424) and size (0x428), big endian.
        BinaryPrimitives.WriteUInt32BigEndian(disc.AsSpan(0x424), fstOffset);
        BinaryPrimitives.WriteUInt32BigEndian(disc.AsSpan(0x428), fstSize);

        var fst = disc.AsSpan(fstOffset);
        // Entry 0: root directory, name offset 0, subtree ends at entry 3.
        BinaryPrimitives.WriteUInt32BigEndian(fst, 0x01000000);
        BinaryPrimitives.WriteUInt32BigEndian(fst[4..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(fst[8..], 3);
        // Entry 1: "sub" directory (parent index 0), subtree ends at entry 3.
        BinaryPrimitives.WriteUInt32BigEndian(fst[12..], 0x01000001);
        BinaryPrimitives.WriteUInt32BigEndian(fst[16..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(fst[20..], 3);
        // Entry 2: "file.bin" (name offset 5) at 0x2000, 16 bytes.
        BinaryPrimitives.WriteUInt32BigEndian(fst[24..], 0x00000005);
        BinaryPrimitives.WriteUInt32BigEndian(fst[28..], fileOffset);
        BinaryPrimitives.WriteUInt32BigEndian(fst[32..], (uint)FileData.Length);

        // Name table at entryCount * 12 = 36: "" / "sub" / "file.bin".
        fst[36] = 0;
        "sub\0"u8.CopyTo(fst[37..]);
        "file.bin\0"u8.CopyTo(fst[41..]);

        FileData.CopyTo(disc.AsSpan(fileOffset));
        return disc;
    }

    [Fact]
    public void TryOpenParsesFileSystemTree()
    {
        using var session = _service.TryOpen(WriteDisc("game.iso"));

        Assert.NotNull(session);
        var sub = Assert.Single(session.Root.Children);
        Assert.Equal("sub", sub.Name);
        Assert.True(sub.IsDirectory);

        var file = Assert.Single(sub.Children);
        Assert.Equal("file.bin", file.Name);
        Assert.False(file.IsDirectory);
        Assert.Equal(FileData.Length, file.Size);
        Assert.Equal(0x2000, file.Offset);
        Assert.Contains("GameCube", session.VolumeSummary);
        Assert.Contains("FST @", session.VolumeSummary);
    }

    [Fact]
    public void CopyNodeToWritesFileBytes()
    {
        using var session = _service.TryOpen(WriteDisc("game.iso"));
        Assert.NotNull(session);
        var file = session.Root.Children[0].Children[0];
        var outputPath = Path.Combine(_tempDir, "out.bin");

        session.CopyNodeTo(file, outputPath, CancellationToken.None);

        Assert.Equal(FileData, File.ReadAllBytes(outputPath));
    }

    [Fact]
    public void CopyNodeToCopiesDirectoryRecursively()
    {
        using var session = _service.TryOpen(WriteDisc("game.iso"));
        Assert.NotNull(session);
        var sub = session.Root.Children[0];
        var outputDir = Path.Combine(_tempDir, "sub-out");

        session.CopyNodeTo(sub, outputDir, CancellationToken.None);

        Assert.Equal(FileData, File.ReadAllBytes(Path.Combine(outputDir, "file.bin")));
    }

    [Fact]
    public void ComputeSha256MatchesExpectedHash()
    {
        using var session = _service.TryOpen(WriteDisc("game.iso"));
        Assert.NotNull(session);
        var file = session.Root.Children[0].Children[0];

        var hash = session.ComputeSha256(file, CancellationToken.None);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(FileData)), hash);
    }

    [Fact]
    public void OpenRvzContainerExploresSameContents()
    {
        var discPath = WriteDisc("game.iso");
        var rvzPath = Path.Combine(_tempDir, "game.rvz");
        var encoder = new RvzSharpService(new LoggerConfiguration().CreateLogger());
        Assert.True(encoder.TryEncode(discPath, rvzPath, "zstd", 5, 131072, scrub: false, progress: null,
            CancellationToken.None));

        using var session = _service.TryOpen(rvzPath);

        Assert.NotNull(session);
        var file = session.Root.Children[0].Children[0];
        var outputPath = Path.Combine(_tempDir, "out-from-rvz.bin");
        session.CopyNodeTo(file, outputPath, CancellationToken.None);
        Assert.Equal(FileData, File.ReadAllBytes(outputPath));
    }

    [Fact]
    public void TryOpenReturnsNullForNonDiscFile()
    {
        var path = Path.Combine(_tempDir, "random.bin");
        File.WriteAllBytes(path, new byte[512]);

        var session = _service.TryOpen(path);

        Assert.Null(session);
        Assert.Contains(_logMessages, static m => m.Contains("Explorer could not open"));
    }

    [Fact]
    public void TryOpenReturnsNullWhenFileSystemIsMissing()
    {
        var disc = new byte[0x3000];
        new Random(7).NextBytes(disc);
        disc[0x1C] = 0xC2;
        disc[0x1D] = 0x33;
        disc[0x1E] = 0x9F;
        disc[0x1F] = 0x3D;
        var path = Path.Combine(_tempDir, "nofst.iso");
        File.WriteAllBytes(path, disc);

        var session = _service.TryOpen(path);

        Assert.Null(session);
        Assert.Contains(_logMessages, static m => m.Contains("Explorer could not open"));
    }

    [Fact]
    public void ExplorerTreeNodeLoadsChildrenOnExpand()
    {
        using var session = _service.TryOpen(WriteDisc("game.iso"));
        Assert.NotNull(session);

        var subNode = new ExplorerTreeNode(session.Root.Children[0]);
        Assert.True(subNode.IsDirectory);
        var placeholder = Assert.Single(subNode.Children);
        Assert.True(placeholder.IsDummy);

        subNode.IsExpanded = true;

        var child = Assert.Single(subNode.Children);
        Assert.Equal("file.bin", child.Name);
        Assert.False(child.IsDirectory);
        Assert.Equal(FileData.Length, child.Size);
        Assert.False(child.IsDummy);
    }
}

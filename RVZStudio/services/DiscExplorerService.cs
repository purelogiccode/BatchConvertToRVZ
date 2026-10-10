using System.Globalization;
using System.Security.Cryptography;
using RVZSharp;
using RVZSharp.Blobs;
using RVZSharp.Files;
using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.Wii;
using Serilog;

namespace RVZStudio.services;

/// <summary>
/// Opens a GameCube/Wii disc image with the RVZSharp library and exposes its file system for
/// the Explorer tab: the parsed FST tree, per-node streaming (copy out) and hashing. The
/// library decodes every supported container (ISO, RVZ, WIA, GCZ, CISO, WBFS, TGC, NFS) to the
/// same canonical disc bytes, so any of them can be browsed.
/// </summary>
public sealed class DiscExplorerService
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscExplorerService"/> class.
    /// </summary>
    /// <param name="logger">The logger used to report explorer activity and failures.</param>
    public DiscExplorerService(ILogger logger)
    {
        _logger = logger.ForContext<DiscExplorerService>();
    }

    /// <summary>
    /// Opens the disc image and parses its file system. Returns null when the image cannot be
    /// opened or has no GameCube/Wii file system (the caller shows the reason from the log).
    /// </summary>
    /// <param name="imagePath">The disc image path (any supported container).</param>
    /// <param name="partitionIndex">The Wii partition to open (ignored for GameCube discs).</param>
    /// <returns>The open session, or null when the image cannot be explored.</returns>
    public DiscExplorerSession? TryOpen(string imagePath, int partitionIndex = 0)
    {
        try
        {
            var session = DiscExplorerSession.Open(imagePath, partitionIndex, _logger);
            _logger.Information("{Message:l}",
                $"Explorer opened {Path.GetFileName(imagePath)} ({session.PartitionCount} partition(s)).");
            return session;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RvzException ex)
        {
            _logger.Information("{Message:l}",
                $"Explorer could not open {Path.GetFileName(imagePath)}: {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Information("{Message:l}",
                $"Explorer could not open {Path.GetFileName(imagePath)}: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Explorer failed to open {ImagePath}", imagePath);
            return null;
        }
    }
}

/// <summary>
/// One open disc image for the Explorer tab. Owns the decoded blob and the parsed file system
/// (one Wii partition at a time); dispose it to release the image. File reads are serialized
/// because most container readers are not thread-safe.
/// </summary>
public sealed class DiscExplorerSession : IDisposable
{
    private readonly IBlobReader _blob;
    private readonly ILogger _logger;
    private readonly Lock _readLock = new();
    private DiscFileSystem _fileSystem;
    private bool _disposed;

    private DiscExplorerSession(
        IBlobReader blob,
        DiscFileSystem fileSystem,
        IReadOnlyList<Partition> partitions,
        int partitionIndex,
        DiscInfo? info,
        string imagePath,
        ILogger logger)
    {
        _blob = blob;
        _fileSystem = fileSystem;
        Partitions = partitions;
        PartitionIndex = partitionIndex;
        Info = info;
        ImagePath = imagePath;
        _logger = logger;
        VolumeSummary = BuildVolumeSummary();
    }

    /// <summary>Gets the path of the open image.</summary>
    public string ImagePath { get; }

    /// <summary>Gets the disc metadata, or null when the header has none.</summary>
    public DiscInfo? Info { get; }

    /// <summary>Gets the disc's Wii partitions (empty for GameCube discs).</summary>
    public IReadOnlyList<Partition> Partitions { get; }

    /// <summary>Gets the currently open partition index (0 for GameCube discs).</summary>
    public int PartitionIndex { get; private set; }

    /// <summary>Gets the number of Wii partitions (0 for GameCube discs).</summary>
    public int PartitionCount => Partitions.Count;

    /// <summary>Gets the file-system root of the open partition.</summary>
    public DiscFileInfo Root => _fileSystem.Root;

    /// <summary>Gets the FST offset (in the decoded image).</summary>
    public ulong FstOffset => _fileSystem.FstOffset;

    /// <summary>Gets the FST size in bytes.</summary>
    public ulong FstSize => _fileSystem.FstSize;

    /// <summary>Gets the human-readable summary line shown above the tree.</summary>
    public string VolumeSummary { get; private set; }

    /// <summary>Opens the image, reads its metadata and parses the selected partition's FST.</summary>
    internal static DiscExplorerSession Open(string imagePath, int partitionIndex, ILogger logger)
    {
        var blob = Blob.Open(imagePath);
        try
        {
            var info = DiscInfo.TryRead(blob);
            var partitions = WiiVolume.GetPartitions(blob);
            var index = partitions.Count == 0 ? 0 : Math.Clamp(partitionIndex, 0, partitions.Count - 1);
            var fileSystem = partitions.Count == 0
                ? DiscFileSystem.Open(blob)
                : DiscFileSystem.Open(blob, partitions[index]);

            return new DiscExplorerSession(blob, fileSystem, partitions, index, info, imagePath, logger);
        }
        catch
        {
            blob.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Switches the open Wii partition and reparses its file system. No-op for GameCube discs
    /// or when the index is unchanged/out of range.
    /// </summary>
    /// <param name="index">The partition index from <see cref="Partitions"/>.</param>
    public void SelectPartition(int index)
    {
        try
        {
            if (Partitions.Count == 0 || index < 0 || index >= Partitions.Count || index == PartitionIndex)
            {
                return;
            }

            var fileSystem = DiscFileSystem.Open(_blob, Partitions[index]);
            var previous = _fileSystem;
            _fileSystem = fileSystem;
            PartitionIndex = index;
            VolumeSummary = BuildVolumeSummary();
            previous.Dispose();
            _logger.Information("{Message:l}",
                $"Explorer switched to partition {PartitionName(Partitions[index].Type)}.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Explorer failed to switch to partition {Index}", index);
            throw;
        }
    }

    /// <summary>Lists the direct children of a directory (the root when null).</summary>
    /// <param name="parent">The directory to list, or null for the root.</param>
    /// <returns>The directory's children (empty for files).</returns>
    public IReadOnlyList<DiscFileInfo> ListChildren(DiscFileInfo? parent)
    {
        try
        {
            var directory = parent ?? _fileSystem.Root;
            return directory.Children;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Explorer failed to list children of {Path}", parent?.Path ?? "/");
            throw;
        }
    }

    /// <summary>
    /// Copies a file or directory out of the image. Directories are copied recursively; entry
    /// names are sanitized so a hostile FST name cannot escape the destination folder.
    /// </summary>
    /// <param name="node">The file or directory to copy.</param>
    /// <param name="destinationPath">The destination file or folder path.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    public void CopyNodeTo(DiscFileInfo node, string destinationPath, CancellationToken cancellationToken)
    {
        try
        {
            CopyNodeToCore(node, destinationPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Explorer failed to copy {Path}", node.Path);
            throw;
        }
    }

    private void CopyNodeToCore(DiscFileInfo node, string destinationPath, CancellationToken cancellationToken)
    {
        if (node.IsDirectory)
        {
            Directory.CreateDirectory(destinationPath);
            foreach (var child in node.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyNodeToCore(child, Path.Combine(destinationPath, SanitizeName(child.Name)), cancellationToken);
            }

            return;
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        lock (_readLock)
        {
            using var output = File.Create(destinationPath);
            _fileSystem.CopyFileTo(node, output, cancellationToken);
        }
    }

    /// <summary>
    /// Computes the SHA-256 of a file's decoded bytes without writing it to disk.
    /// </summary>
    /// <param name="file">The file to hash.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The hash as an uppercase hex string.</returns>
    public string ComputeSha256(DiscFileInfo file, CancellationToken cancellationToken)
    {
        try
        {
            if (file.IsDirectory)
            {
                throw new ArgumentException("Directories cannot be hashed.", nameof(file));
            }

            lock (_readLock)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var sink = new HashingStream(hash);
                _fileSystem.CopyFileTo(file, sink, cancellationToken);
                return Convert.ToHexString(hash.GetHashAndReset());
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Explorer failed to hash {Path}", file.Path);
            throw;
        }
    }

    private string BuildVolumeSummary()
    {
        var parts = new List<string>
        {
            $"{Blob.GetName(_blob.Type)} image",
            $"{FormatSize(_blob.Length)} ({_blob.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes)"
        };

        if (_blob.BlockSize > 0)
        {
            parts.Add($"block size {FormatSize(_blob.BlockSize)}");
        }

        if (Info is not null)
        {
            parts.Add(
                $"{Info.DiscType} disc: {Info.GameId} \"{Info.InternalName}\" ({Info.Region}, rev {Info.Revision})");
        }

        if (Partitions.Count > 0)
        {
            var partition = Partitions[PartitionIndex];
            parts.Add(
                $"partition {PartitionName(partition.Type)} @ 0x{partition.Offset:X} ({FormatSize((long)partition.DataSize)})");
        }

        parts.Add($"FST @ 0x{FstOffset:X} ({FstSize.ToString("N0", CultureInfo.InvariantCulture)} bytes)");
        return string.Join("  •  ", parts);
    }

    /// <summary>Maps a Wii partition type to its Dolphin name.</summary>
    public static string PartitionName(uint type)
    {
        return type switch
        {
            0 => "game",
            1 => "update",
            2 => "channel",
            _ => $"type {type.ToString(CultureInfo.InvariantCulture)}"
        };
    }

    /// <summary>
    /// Replaces characters that cannot appear in a local file name (and path separators) so
    /// image-internal names cannot traverse outside the chosen destination.
    /// </summary>
    private static string SanitizeName(string name)
    {
        var sanitized = name.Replace('/', '_').Replace('\\', '_');
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or ".." ? "_" : sanitized;
    }

    private static string FormatSize(long bytes)
    {
        string[] suffix = ["B", "KB", "MB", "GB", "TB"];
        int i;
        double value = bytes;
        for (i = 0; i < suffix.Length - 1 && bytes >= 1024; i++, bytes /= 1024)
        {
            value = bytes / 1024.0;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {suffix[i]}");
    }

    /// <summary>Disposes the parsed file system and the decoded image.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _fileSystem.Dispose();
            _blob.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Error disposing explorer session for {ImagePath}", ImagePath);
        }
    }

    /// <summary>Write-only stream that feeds every byte into an <see cref="IncrementalHash"/>.</summary>
    private sealed class HashingStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
    }
}

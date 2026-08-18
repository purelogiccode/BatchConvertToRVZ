using System.IO;
using RVZSharp;
using RVZSharp.Blobs;
using RVZSharp.Models;
using Serilog;

namespace BatchConvertToRVZ.services;

/// <summary>
/// Uses the RVZSharp library to natively encode (disc image -&gt; RVZ) and decode (RVZ -&gt; ISO)
/// disc images without shelling out to DolphinTool.
/// <para>
/// Every library failure is logged at Error level so it is automatically forwarded to the
/// Bug Report API by <see cref="BugReportSink"/>; callers must fall back to DolphinTool
/// when this service reports failure. Environmental failures (I/O, permissions) are logged
/// at Information level and do not produce bug reports.
/// </para>
/// </summary>
public class RvzSharpService
{
    private readonly ILogger _logger;

    // Input formats the library is known to read. NKIT containers (.nkit.iso / .nkit.gcz)
    // are excluded because RVZSharp does not understand the NKIT format.
    private static readonly HashSet<string> SupportedInputExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".iso", ".gcm", ".wbfs", ".gcz", ".wia", ".rvz"
    };

    // Compression methods the library can write. zlib and lz4 are DolphinTool-only.
    private static readonly HashSet<string> SupportedCompressionMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "zstd", "bzip2", "lzma", "lzma2"
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="RvzSharpService"/> class.
    /// </summary>
    /// <param name="logger">The logger used to report conversion activity and failures.</param>
    public RvzSharpService(ILogger logger)
    {
        _logger = logger.ForContext<RvzSharpService>();
    }

    /// <summary>
    /// Returns true when the library can encode the given input file with the given settings.
    /// </summary>
    /// <param name="inputFile">The input disc image path.</param>
    /// <param name="compressionMethod">The requested compression method (zstd, bzip2, lzma, lzma2, zlib, lz4).</param>
    /// <returns>true if RVZSharp should attempt the encoding; otherwise, false.</returns>
    public bool CanEncode(string inputFile, string compressionMethod)
    {
        var fileName = Path.GetFileName(inputFile);

        // NKIT containers (.nkit.iso / .nkit.gcz) are not understood by RVZSharp.
        if (fileName.EndsWith(".nkit.iso", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".nkit.gcz", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return SupportedInputExtensions.Contains(Path.GetExtension(inputFile))
               && SupportedCompressionMethods.Contains(compressionMethod);
    }

    /// <summary>
    /// Encodes a disc image to RVZ using the library.
    /// </summary>
    /// <param name="inputFile">The input disc image path.</param>
    /// <param name="outputFile">The destination RVZ file path.</param>
    /// <param name="compressionMethod">The compression method (zstd, bzip2, lzma or lzma2).</param>
    /// <param name="compressionLevel">The compression level.</param>
    /// <param name="blockSize">The chunk/block size in bytes.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>true on success; false when the library failed and DolphinTool should be used instead.</returns>
    public bool TryEncode(
        string inputFile,
        string outputFile,
        string compressionMethod,
        int compressionLevel,
        int blockSize,
        CancellationToken cancellationToken)
    {
        try
        {
            // Pre-validation: the input must actually look like the disc format its
            // extension claims. Plain disc images (ISO/GCM) must carry a GameCube/Wii
            // disc header (Wii magic 0x5D1C9EA3 at offset 0x18, GameCube magic 0xC2339F3D
            // at offset 0x1C); container formats must start with their container magic.
            // The library validates the disc header before writing too, but pre-validating
            // here keeps expected failures quiet (Information log, no bug report) so
            // non-disc files fall back to DolphinTool without bug-report noise.
            if (!IsRecognizedDiscImage(inputFile))
            {
                _logger.Information(
                    "Input {FileName} is not a recognized disc image (missing GameCube/Wii header or container magic). Falling back to DolphinTool.",
                    Path.GetFileName(inputFile));
                return false;
            }

            using var input = Blob.Open(inputFile);
            using var output = File.Create(outputFile);

            var options = new RvzWriteOptions
            {
                Compression = MapCompressionMethod(compressionMethod),
                CompressionLevel = compressionLevel,
                ChunkSize = blockSize,
                Packing = true
            };

            RvzWriter.Write(input, output, options, cancellationToken: cancellationToken);

            _logger.Information("Converted to RVZ using RVZSharp: {FileName}", Path.GetFileName(inputFile));
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDeletePartialOutput(outputFile);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeletePartialOutput(outputFile);
            _logger.Information(
                "RVZSharp library could not encode {FileName}: {Message}. Falling back to DolphinTool.",
                Path.GetFileName(inputFile), ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            TryDeletePartialOutput(outputFile);
            _logger.Error(ex,
                "RVZSharp library failed while encoding {FileName} (compression: {CompressionMethod}, level: {CompressionLevel}, block size: {BlockSize}). Falling back to DolphinTool.",
                Path.GetFileName(inputFile), compressionMethod, compressionLevel, blockSize);
            return false;
        }
    }

    /// <summary>
    /// Returns true when the library can decode the given input file.
    /// </summary>
    /// <param name="inputFile">The input RVZ (or WIA) file path.</param>
    /// <returns>true if RVZSharp should attempt the decoding; otherwise, false.</returns>
    public bool CanDecode(string inputFile)
    {
        var extension = Path.GetExtension(inputFile);
        return extension.Equals(".rvz", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".wia", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decodes an RVZ file back to the original disc image (ISO) using the library.
    /// </summary>
    /// <param name="inputFile">The input RVZ file path.</param>
    /// <param name="outputFile">The destination ISO file path.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>true on success; false when the library failed and DolphinTool should be used instead.</returns>
    public bool TryDecodeToIso(
        string inputFile,
        string outputFile,
        CancellationToken cancellationToken)
    {
        try
        {
            using var reader = RvzReader.Open(File.OpenRead(inputFile), leaveOpen: false);
            using var output = File.Create(outputFile);

            var buffer = new byte[1024 * 1024];
            long remaining = reader.Length;
            long position = 0;

            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var read = reader.ReadAt(position, buffer.AsSpan(0, (int)Math.Min(buffer.Length, remaining)));
                if (read <= 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
                position += read;
                remaining -= read;
            }

            _logger.Information("Converted to ISO using RVZSharp: {FileName}", Path.GetFileName(inputFile));
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDeletePartialOutput(outputFile);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeletePartialOutput(outputFile);
            _logger.Information(
                "RVZSharp library could not decode {FileName}: {Message}. Falling back to DolphinTool.",
                Path.GetFileName(inputFile), ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            TryDeletePartialOutput(outputFile);
            _logger.Error(ex,
                "RVZSharp library failed while decoding {FileName}. Falling back to DolphinTool.",
                Path.GetFileName(inputFile));
            return false;
        }
    }

    // First bytes of each container format the library can read. The magic must
    // actually be present or Blob.Open would silently treat the file as a plain ISO.
    // (Note: the GCZ magic is 0xB10BC001 little endian — not the ASCII "GCZ\0" the
    // name suggests — and the RVZ/WIA magics are "RVZ\1"/"WIA\1", not "RVZ\0"/"WIA\0".)
    private static readonly Dictionary<string, byte[]> ContainerMagics = new(StringComparer.OrdinalIgnoreCase)
    {
        [".wbfs"] = "WBFS"u8.ToArray(),
        [".gcz"] = [0x01, 0xC0, 0x0B, 0xB1],
        [".wia"] = "WIA\x01"u8.ToArray(),
        [".rvz"] = "RVZ\x01"u8.ToArray()
    };

    /// <summary>
    /// Returns true when the file actually looks like the disc format its extension
    /// claims: plain images (ISO/GCM) need a GameCube/Wii disc header, containers
    /// need their container magic.
    /// </summary>
    private static bool IsRecognizedDiscImage(string inputFile)
    {
        var extension = Path.GetExtension(inputFile);

        if (extension.Equals(".iso", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gcm", StringComparison.OrdinalIgnoreCase))
        {
            return HasValidDiscHeader(inputFile);
        }

        return ContainerMagics.TryGetValue(extension, out var magic)
               && HasMagicPrefix(inputFile, magic);
    }

    /// <summary>
    /// Checks that the file starts with a valid GameCube/Wii disc header. The header magic
    /// is stored big endian: Wii 0x5D1C9EA3 at offset 0x18, GameCube 0xC2339F3D at offset
    /// 0x1C (Dolphin: TryCreateDisc, Volume.cpp) — matching the library's validation.
    /// </summary>
    private static bool HasValidDiscHeader(string inputFile)
    {
        try
        {
            using var stream = File.OpenRead(inputFile);

            Span<byte> header = stackalloc byte[0x20];
            var read = stream.Read(header);
            if (read < header.Length)
            {
                return false;
            }

            return ReadBe32(header, 0x18) == 0x5D1C9EA3u || ReadBe32(header, 0x1C) == 0xC2339F3Du;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static uint ReadBe32(ReadOnlySpan<byte> data, int offset)
    {
        return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
    }

    /// <summary>
    /// Checks that the file starts with the given container magic bytes.
    /// </summary>
    private static bool HasMagicPrefix(string inputFile, ReadOnlySpan<byte> magic)
    {
        try
        {
            using var stream = File.OpenRead(inputFile);

            Span<byte> prefix = stackalloc byte[magic.Length];
            var read = stream.Read(prefix);
            return read >= prefix.Length && prefix.SequenceEqual(magic);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Maps an application compression method name to the library's <see cref="CompressionType"/>.
    /// </summary>
    private static CompressionType MapCompressionMethod(string compressionMethod)
    {
        return compressionMethod.ToLowerInvariant() switch
        {
            "bzip2" => CompressionType.Bzip2,
            "lzma" => CompressionType.Lzma,
            "lzma2" => CompressionType.Lzma2,
            _ => CompressionType.Zstd
        };
    }

    /// <summary>
    /// Deletes a partially written output file left behind by a failed or canceled attempt.
    /// </summary>
    private static void TryDeletePartialOutput(string outputFile)
    {
        try
        {
            if (File.Exists(outputFile))
            {
                File.Delete(outputFile);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
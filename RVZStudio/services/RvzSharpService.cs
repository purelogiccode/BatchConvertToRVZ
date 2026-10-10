using System.Globalization;
using RVZSharp;
using RVZSharp.Blobs;
using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.Verification;
using Serilog;

namespace RVZStudio.services;

/// <summary>
/// Uses the RVZSharp library to natively encode (disc image -&gt; RVZ), decode (any container -&gt;
/// ISO/WIA/GCZ/WBFS/CISO/TGC) and verify (Dolphin's volume verifier) disc images without
/// shelling out to DolphinTool.
/// <para>
/// RVZSharp is the primary engine; every method reports failure so the caller can fall back
/// to DolphinTool where it supports the operation. Expected user-input failures (corrupt
/// files, format/container mismatches) are logged at Information level; unexpected library
/// failures are logged at Error level so they are automatically forwarded to the Bug Report
/// API by <see cref="BugReportSink"/>.
/// </para>
/// </summary>
public class RvzSharpService
{
    private readonly ILogger _logger;

    // Input formats the library is known to read. NKIT containers (.nkit.iso / .nkit.gcz)
    // are excluded because RVZSharp does not understand the NKIT format.
    private static readonly HashSet<string> SupportedInputExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".iso", ".gcm", ".wbfs", ".gcz", ".wia", ".rvz", ".ciso", ".wbi", ".tgc", ".nfs"
    };

    // Compression methods the library can write. zlib and lz4 are DolphinTool-only.
    private static readonly HashSet<string> SupportedCompressionMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "zstd", "bzip2", "lzma", "lzma2"
    };

    // Output formats the library can write (the extraction tab's choices).
    private static readonly HashSet<string> SupportedOutputFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "iso", "wia", "gcz", "wbfs", "ciso", "tgc"
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
    public static bool CanEncode(string inputFile, string compressionMethod)
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
    /// <param name="scrub">Whether to zero the data of non-game Wii partitions before encoding.</param>
    /// <param name="progress">Optional progress receiver (fraction in [0, 1]).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>true on success; false when the library failed and DolphinTool should be used instead.</returns>
    public bool TryEncode(
        string inputFile,
        string outputFile,
        string compressionMethod,
        int compressionLevel,
        int blockSize,
        bool scrub,
        IProgress<double>? progress,
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
            LogDiscInfo(input, inputFile);
            using var output = File.Create(outputFile);

            var options = new RvzWriteOptions
            {
                Compression = MapCompressionMethod(compressionMethod),
                CompressionLevel = compressionLevel,
                ChunkSize = blockSize,
                Packing = true,
                Scrub = scrub
            };

            RvzWriter.Write(input, output, options, progress, cancellationToken);

            _logger.Information("Converted to RVZ using RVZSharp: {FileName}", Path.GetFileName(inputFile));
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDeletePartialOutput(outputFile);
            throw;
        }
        catch (RvzException ex)
        {
            // Expected user-input failures: corrupt containers, unsupported versions,
            // NFS images whose code/htk.bin key file is missing, ...
            TryDeletePartialOutput(outputFile);
            _logger.Information(
                "RVZSharp could not encode {FileName}: {Message}. Falling back to DolphinTool.",
                Path.GetFileName(inputFile), ex.Message);
            return false;
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
                "RVZSharp library failed while encoding {FileName} (compression: {CompressionMethod}, level: {CompressionLevel}, block size: {BlockSize}, scrub: {Scrub}). Falling back to DolphinTool.",
                Path.GetFileName(inputFile), compressionMethod, compressionLevel, blockSize, scrub);
            return false;
        }
    }

    /// <summary>
    /// Returns true when the library can decode the given input file to another disc format.
    /// </summary>
    /// <param name="inputFile">The input RVZ (or WIA) file path.</param>
    /// <returns>true if RVZSharp should attempt the decoding; otherwise, false.</returns>
    public static bool CanDecode(string inputFile)
    {
        var extension = Path.GetExtension(inputFile);
        return extension.Equals(".rvz", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".wia", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decodes an RVZ/WIA file to the requested output format using the library. ISO output
    /// uses the parallel full-image decode; the other formats use the matching writer
    /// (WIA, GCZ, WBFS, CISO or TGC).
    /// </summary>
    /// <param name="inputFile">The input RVZ/WIA file path.</param>
    /// <param name="outputFile">The destination file path.</param>
    /// <param name="outputFormat">The output format (iso, wia, gcz, wbfs, ciso or tgc).</param>
    /// <param name="scrub">Whether to zero the data of non-game Wii partitions before writing.</param>
    /// <param name="progress">Optional progress receiver (fraction in [0, 1]).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>true on success; false when the library failed and DolphinTool should be used instead.</returns>
    public bool TryDecode(
        string inputFile,
        string outputFile,
        string outputFormat,
        bool scrub,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var format = outputFormat.ToLowerInvariant();

        if (!SupportedOutputFormats.Contains(format))
        {
            _logger.Information(
                "RVZSharp cannot write output format {OutputFormat} for {FileName}. Falling back to DolphinTool.",
                outputFormat, Path.GetFileName(inputFile));
            return false;
        }

        try
        {
            using var input = Blob.Open(inputFile);

            // A file with no recognized container magic opens as a plain blob; reject
            // anything that is not actually a GameCube/Wii disc so corrupt or renamed
            // files fall back to DolphinTool instead of being written as garbage.
            if (!Blob.IsDisc(input))
            {
                _logger.Information(
                    "Input {FileName} is not a GameCube/Wii disc image. Falling back to DolphinTool.",
                    Path.GetFileName(inputFile));
                return false;
            }

            LogDiscInfo(input, inputFile);
            using var output = File.Create(outputFile);

            switch (format)
            {
                case "iso":
                    // Parallel full-image decode where the container supports it.
                    input.CopyTo(output, progress, maxThreads: 0, cancellationToken);
                    break;
                case "wia":
                    WiaWriter.Write(input, output, new RvzWriteOptions
                    {
                        Compression = CompressionType.Lzma2,
                        CompressionLevel = 5,
                        ChunkSize = 0x200000,
                        Scrub = scrub
                    }, progress, cancellationToken);
                    break;
                case "gcz":
                    GczWriter.Write(input, output, new GczWriteOptions
                    {
                        Scrub = scrub
                    }, progress, cancellationToken);
                    break;
                case "wbfs":
                    WbfsWriter.Write(input, output, new WbfsWriteOptions
                    {
                        Scrub = scrub
                    }, progress, cancellationToken);
                    break;
                case "ciso":
                    CisoWriter.Write(input, output, new CisoWriteOptions
                    {
                        Scrub = scrub
                    }, progress, cancellationToken);
                    break;
                case "tgc":
                    TgcWriter.Write(input, output, progress, cancellationToken);
                    break;
            }

            _logger.Information("{Message:l}",
                $"Converted to {format.ToUpperInvariant()} using RVZSharp: {Path.GetFileName(inputFile)}");
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDeletePartialOutput(outputFile);
            throw;
        }
        catch (RvzException ex)
        {
            // Expected user-input failures: corrupt containers, Wii-only formats on
            // GameCube discs (WBFS), GameCube-only formats on Wii discs (TGC), ...
            TryDeletePartialOutput(outputFile);
            _logger.Information(
                "RVZSharp could not write {OutputFormat} for {FileName}: {Message}. Falling back to DolphinTool.",
                format.ToUpperInvariant(), Path.GetFileName(inputFile), ex.Message);
            return false;
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
                "RVZSharp library failed while writing {OutputFormat} for {FileName}. Falling back to DolphinTool.",
                format.ToUpperInvariant(), Path.GetFileName(inputFile));
            return false;
        }
    }

    /// <summary>
    /// Verifies a disc image with the library (Dolphin's volume verifier: partition headers,
    /// TMD/H3 tables and the h0/h1/h2/h3 hash trees) and logs the decoded image's hashes.
    /// </summary>
    /// <param name="inputFile">The input RVZ/WIA file path.</param>
    /// <param name="progress">Optional progress receiver (fraction in [0, 1]).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// true when the disc verified cleanly; false when verification completed and found
    /// problems (or the container is damaged); null when the library could not run the
    /// verification and the caller should fall back to DolphinTool.
    /// </returns>
    public bool? TryVerify(string inputFile, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(inputFile);

        try
        {
            using var input = Blob.Open(inputFile);

            if (!Blob.IsDisc(input))
            {
                _logger.Information("Verification failed for {FileName}: not a GameCube/Wii disc image.", fileName);
                return false;
            }

            LogDiscInfo(input, inputFile);

            var report = DiscVerifier.Verify(input, progress, cancellationToken);
            LogVerificationReport(report, fileName);
            LogHashes(input, fileName, progress, cancellationToken);

            return report.IsValid;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RvzUnsupportedException ex)
        {
            // A newer container version: DolphinTool may still support it.
            _logger.Information(
                "RVZSharp cannot verify {FileName}: {Message}. Falling back to DolphinTool.",
                fileName, ex.Message);
            return null;
        }
        catch (RvzException ex)
        {
            // A damaged container (format or hash mismatch): the file is corrupt.
            _logger.Information("Verification failed for {FileName}: {Message}", fileName, ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Information(
                "RVZSharp could not verify {FileName}: {Message}. Falling back to DolphinTool.",
                fileName, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "RVZSharp library failed while verifying {FileName}. Falling back to DolphinTool.",
                fileName);
            return null;
        }
    }

    /// <summary>
    /// Logs the disc metadata (game ID, internal name, region and revision) of an opened blob.
    /// </summary>
    private void LogDiscInfo(IBlobReader input, string inputFile)
    {
        try
        {
            var info = DiscInfo.TryRead(input);
            if (info is null) return;

            _logger.Information("{Message:l}",
                $"Disc info for {Path.GetFileName(inputFile)}: {info.GameId} \"{info.InternalName}\" ({info.Region}, {info.Country}, rev {info.Revision}).");
        }
        catch (Exception ex)
        {
            // Metadata is informational only; never fail the operation over it.
            _logger.Information("{Message:l}", $"Could not read disc info for {Path.GetFileName(inputFile)}: {ex.Message}");
        }
    }

    /// <summary>
    /// Logs a verification report: per-partition results and every reported issue.
    /// </summary>
    private void LogVerificationReport(VerificationReport report, string fileName)
    {
        if (report.DiscType == DiscType.GameCube)
        {
            _logger.Information("{Message:l}",
                $"Verified {fileName}: GameCube disc (no partition hash trees).");
        }
        else
        {
            foreach (var partition in report.Partitions)
            {
                _logger.Information("{Message:l}",
                    $"Partition {partition.Name}: {partition.VerifiedBlocks}/{partition.Blocks} blocks verified, {partition.FailedBlocks} failed, TMD valid: {partition.TmdValid}, H3 table valid: {partition.H3TableValid}.");
            }
        }

        foreach (var issue in report.Issues)
        {
            _logger.Information("{Message:l}", $"[{issue.Severity}] {issue.Message}");
        }

        foreach (var issue in report.Partitions.SelectMany(static p => p.Issues))
        {
            _logger.Information("{Message:l}", $"[{issue.Severity}] {issue.Message}");
        }
    }

    /// <summary>
    /// Logs the CRC-32, MD5 and SHA-1 of the decoded image (the same digests Dolphin's
    /// volume verifier reports).
    /// </summary>
    private void LogHashes(IBlobReader input, string fileName, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var hashes = DiscHasher.Compute(input, progress, cancellationToken);
            _logger.Information("{Message:l}",
                $"Hashes for {fileName}: CRC32={hashes.Crc32.ToString("X8", CultureInfo.InvariantCulture)}, MD5={Convert.ToHexString(hashes.Md5)}, SHA1={Convert.ToHexString(hashes.Sha1)}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Hashing is informational only; never fail verification over it.
            _logger.Information("{Message:l}", $"Could not compute hashes for {fileName}: {ex.Message}");
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
        [".rvz"] = "RVZ\x01"u8.ToArray(),
        [".ciso"] = "CISO"u8.ToArray(),
        [".wbi"] = "CISO"u8.ToArray(),
        [".tgc"] = [0xAE, 0x0F, 0x38, 0xA2],
        [".nfs"] = "EGGS"u8.ToArray()
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

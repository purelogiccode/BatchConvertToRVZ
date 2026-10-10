using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Serilog;
using SharpCompress.Archives;

namespace RVZStudio.services;

/// <summary>
/// Converts disc images (and archives containing them) to RVZ. RVZSharp is the primary
/// engine; DolphinTool is used as a fallback when the native engine cannot handle a file.
/// </summary>
public class ConversionService
{
    private readonly ILogger _logger;
    private readonly RvzSharpService _rvzSharpService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversionService"/> class.
    /// </summary>
    /// <param name="logger">The logger used to report conversion activity and failures.</param>
    /// <param name="rvzSharpService">The native engine, or null to create a new one.</param>
    public ConversionService(ILogger logger, RvzSharpService? rvzSharpService = null)
    {
        _logger = logger.ForContext<ConversionService>();
        _rvzSharpService = rvzSharpService ?? new RvzSharpService(logger);
    }

    /// <summary>
    /// Converts a batch of files to RVZ, reporting progress and per-file success/failure.
    /// </summary>
    /// <param name="dolphinToolPath">Path to the optional DolphinTool fallback executable.</param>
    /// <param name="files">The input file paths to convert.</param>
    /// <param name="outputFolder">The folder that receives the converted RVZ files.</param>
    /// <param name="deleteFiles">Whether to delete each source file after a successful conversion.</param>
    /// <param name="compressionMethod">The RVZ compression method (zstd, bzip2, lzma or lzma2).</param>
    /// <param name="compressionLevel">The compression level.</param>
    /// <param name="blockSize">The RVZ block size in bytes.</param>
    /// <param name="updateProgress">Receives (processed, total, currentFilePath) after each file.</param>
    /// <param name="incrementSuccess">Called with the number of newly succeeded files.</param>
    /// <param name="incrementFailure">Called with the number of newly failed files.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <param name="scrub">Whether to zero non-game Wii partitions before encoding.</param>
    /// <param name="fileProgress">Optional per-file progress receiver (fraction in [0, 1]).</param>
    public async Task PerformBatchConversionAsync(
        string dolphinToolPath,
        string[] files,
        string outputFolder,
        bool deleteFiles,
        string compressionMethod,
        int compressionLevel,
        int blockSize,
        Action<int, int, string> updateProgress,
        Action<int> incrementSuccess,
        Action<int> incrementFailure,
        CancellationToken cancellationToken,
        bool scrub = false,
        IProgress<double>? fileProgress = null)
    {
        try
        {
            _logger.Information("{Message:l}", "Preparing for batch conversion...");

            var totalFilesToProcess = files.Length;
            _logger.Information("{Message:l}", $"Processing {totalFilesToProcess} selected files.");

            if (totalFilesToProcess == 0)
            {
                _logger.Information("{Message:l}", "No files selected for conversion.");
                return;
            }

            // Ensure the destination exists so the engine can write its output directly.
            Directory.CreateDirectory(outputFolder);

            var filesProcessedCount = 0;

            _logger.Information("{Message:l}", "Processing files sequentially.");
            foreach (var inputFile in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileName = Path.GetFileName(inputFile);
                _logger.Information("{Message:l}", $"Processing: {fileName}");

                fileProgress?.Report(0);

                var success = await ProcessFileAsync(
                    dolphinToolPath,
                    inputFile,
                    outputFolder,
                    deleteFiles,
                    compressionMethod,
                    compressionLevel,
                    blockSize,
                    scrub,
                    fileProgress,
                    cancellationToken);

                if (success)
                {
                    incrementSuccess(1);
                    _logger.Information("{Message:l}", $"Conversion successful: {fileName}");
                }
                else
                {
                    incrementFailure(1);
                    _logger.Information("{Message:l}", $"Conversion failed: {fileName}");
                }

                filesProcessedCount++;
                updateProgress(filesProcessedCount, totalFilesToProcess, inputFile);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("{Message:l}", "Batch conversion operation was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error during batch conversion operation: {Message}", ex.Message);
        }
    }

    private async Task<bool> ProcessFileAsync(
        string dolphinToolPath,
        string inputFile,
        string outputFolder,
        bool deleteOriginal,
        string compressionMethod,
        int compressionLevel,
        int blockSize,
        bool scrub,
        IProgress<double>? fileProgress,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(inputFile);
        var inputExtension = Path.GetExtension(inputFile).ToLowerInvariant();

        try
        {
            if (FileService.GetArchiveExtensions().Contains(inputExtension))
            {
                return await ProcessArchiveFileAsync(
                    dolphinToolPath,
                    inputFile,
                    outputFolder,
                    deleteOriginal,
                    compressionMethod,
                    compressionLevel,
                    blockSize,
                    scrub,
                    fileProgress,
                    cancellationToken);
            }
            else
            {
                return await ConvertSingleFileAsync(
                    dolphinToolPath,
                    inputFile,
                    outputFolder,
                    deleteOriginal,
                    compressionMethod,
                    compressionLevel,
                    blockSize,
                    scrub,
                    fileProgress,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("{Message:l}", $"Processing canceled: {fileName}");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing file {FileName}: {Message}", fileName, ex.Message);
            return false;
        }
    }

    private async Task<bool> ProcessArchiveFileAsync(
        string dolphinToolPath,
        string archivePath,
        string outputFolder,
        bool deleteOriginal,
        string compressionMethod,
        int compressionLevel,
        int blockSize,
        bool scrub,
        IProgress<double>? fileProgress,
        CancellationToken cancellationToken)
    {
        var archiveFileName = Path.GetFileName(archivePath);

        try
        {
            _logger.Information("{Message:l}", $"Extracting archive: {archiveFileName}");

            var extractionResult = await ExtractArchiveAsync(archivePath, cancellationToken);
            if (!extractionResult.Success)
            {
                _logger.Information("{Message:l}",
                    $"Failed to extract {archiveFileName}: {extractionResult.ErrorMessage}");
                return false;
            }

            var extractedFilePath = extractionResult.FilePath;
            var tempDir = extractionResult.TempDir;
            var isRvzFile = extractionResult.IsRvzFile;

            try
            {
                bool success;

                if (isRvzFile)
                {
                    var fileName = Path.GetFileName(extractedFilePath);
                    var outputFile = Path.Combine(outputFolder, fileName);

                    _logger.Information("{Message:l}", $"Found RVZ file inside archive, copying directly: {fileName}");

                    try
                    {
                        Directory.CreateDirectory(outputFolder);
                        File.Copy(extractedFilePath, outputFile, true);
                        _logger.Information("{Message:l}", $"Successfully copied RVZ file from archive: {fileName}");

                        success = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.Information("{Message:l}",
                            $"Error copying RVZ file from archive {fileName}: {ex.Message}");
                        success = false;
                    }
                }
                else
                {
                    success = await ConvertSingleFileAsync(
                        dolphinToolPath,
                        extractedFilePath,
                        outputFolder,
                        false,
                        compressionMethod,
                        compressionLevel,
                        blockSize,
                        scrub,
                        fileProgress,
                        cancellationToken);
                }

                if (success && deleteOriginal)
                {
                    await TryDeleteFileAsync(archivePath, "original archive");
                }

                return success;
            }
            finally
            {
                await TryDeleteDirectoryAsync(tempDir, "temporary extraction directory");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("{Message:l}", $"Archive processing canceled: {archiveFileName}");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing archive {ArchiveFileName}: {Message}", archiveFileName, ex.Message);
            return false;
        }
    }

    private async Task<bool> ConvertSingleFileAsync(
        string dolphinToolPath,
        string inputFile,
        string outputFolder,
        bool deleteOriginal,
        string compressionMethod,
        int compressionLevel,
        int blockSize,
        bool scrub,
        IProgress<double>? fileProgress,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(inputFile);
        var outputFileName = FileService.GetBaseFileNameWithoutGameExtension(fileName) + ".rvz";
        var outputFile = Path.Combine(outputFolder, outputFileName);

        try
        {
            if (FileService.IsRvzFile(inputFile))
            {
                _logger.Information("{Message:l}",
                    $"File is already in RVZ format, copying: {fileName} -> {outputFileName}");

                try
                {
                    Directory.CreateDirectory(outputFolder);
                    File.Copy(inputFile, outputFile, true);
                    fileProgress?.Report(1);
                    _logger.Information("{Message:l}", $"Successfully copied RVZ file: {fileName}");

                    if (deleteOriginal)
                    {
                        await TryDeleteFileAsync(inputFile, "original file");
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Information("{Message:l}", $"Error copying RVZ file {fileName}: {ex.Message}");
                    return false;
                }
            }
            else
            {
                _logger.Information("{Message:l}", $"Converting: {fileName} -> {outputFileName}");

                var success = await ConvertToRvzAsync(
                    dolphinToolPath,
                    inputFile,
                    outputFile,
                    compressionMethod,
                    compressionLevel,
                    blockSize,
                    scrub,
                    fileProgress,
                    cancellationToken);

                if (success && deleteOriginal)
                {
                    await TryDeleteFileAsync(inputFile, "original file");
                }

                return success;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("{Message:l}", $"Conversion canceled: {fileName}");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error converting file {FileName}: {Message}", fileName, ex.Message);
            return false;
        }
    }

    private async Task<bool> ConvertToRvzAsync(
        string dolphinToolPath,
        string inputFile,
        string outputFile,
        string compressionMethod,
        int compressionLevel,
        int blockSize,
        bool scrub,
        IProgress<double>? fileProgress,
        CancellationToken cancellationToken)
    {
        if (RvzSharpService.CanEncode(inputFile, compressionMethod)
            && _rvzSharpService.TryEncode(inputFile, outputFile, compressionMethod, compressionLevel, blockSize,
                scrub, fileProgress, cancellationToken))
        {
            return true;
        }

        if (!ProcessHelper.ExecutableExists(dolphinToolPath))
        {
            // DolphinTool is an optional fallback; a missing helper is an expected environment
            // condition, so log it at Information level (no bug report) and fail this file.
            _logger.Information("{Message:l}", ProcessHelper.GetMissingExecutableMessage(dolphinToolPath));
            return false;
        }

        ProcessHelper.EnsureExecutable(dolphinToolPath);

        using var process = new Process();

        try
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = dolphinToolPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.StartInfo.ArgumentList.Add("convert");
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(inputFile);
            process.StartInfo.ArgumentList.Add("-o");
            process.StartInfo.ArgumentList.Add(outputFile);
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add("rvz");
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(compressionMethod);
            process.StartInfo.ArgumentList.Add("-l");
            process.StartInfo.ArgumentList.Add(compressionLevel.ToString(CultureInfo.InvariantCulture));
            process.StartInfo.ArgumentList.Add("-b");
            process.StartInfo.ArgumentList.Add(blockSize.ToString(CultureInfo.InvariantCulture));

            process.EnableRaisingEvents = true;

            var outputChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            var outputCompleted = new TaskCompletionSource<bool>();
            var errorCompleted = new TaskCompletionSource<bool>();

            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is null)
                {
                    outputCompleted.TrySetResult(true);
                }
                else
                {
                    outputChannel.Writer.TryWrite(args.Data);
                    _logger.Information("{Message:l}", $"[DolphinTool] {args.Data}");
                }
            };

            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is null)
                {
                    errorCompleted.TrySetResult(true);
                }
                else
                {
                    outputChannel.Writer.TryWrite(args.Data);
                    _logger.Information("{Message:l}", $"[DolphinTool ERROR] {args.Data}");
                }
            };

            ProcessHelper.SuppressErrorDialogs();
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputCompleted.Task, errorCompleted.Task);

            outputChannel.Writer.Complete();

            var outputBuilder = new StringBuilder();
            await foreach (var line in outputChannel.Reader.ReadAllAsync(cancellationToken))
            {
                outputBuilder.AppendLine(line);
            }

            var output = outputBuilder.ToString();
            if (process.ExitCode == 0)
            {
                _logger.Information("{Message:l}", $"Successfully converted to RVZ: {Path.GetFileName(inputFile)}");
                return true;
            }
            else
            {
                _logger.Information("{Message:l}",
                    $"Conversion failed for {Path.GetFileName(inputFile)}. Exit code: {process.ExitCode}");
                _logger.Information("{Message:l}", $"Output: {output}");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch
            {
                // ignored
            }

            throw;
        }
        catch (Exception ex)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch
            {
                /* process may not have started */
            }

            _logger.Information("{Message:l}", $"DolphinTool process error: {ex.Message}");
            return false;
        }
    }

    private async Task<(bool Success, string FilePath, string TempDir, string ErrorMessage, bool IsRvzFile)>
        ExtractArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        var tempDir = string.Empty;

        try
        {
            tempDir = Path.Combine(Path.GetTempPath(), "RVZStudio_Extract_" + Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);

            _logger.Information("{Message:l}", $"Extracting archive to temporary directory: {tempDir}");

            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var supportedExtensions = FileService.GetPrimaryTargetExtensionsInsideArchive();
            var rvzExtensions = FileService.GetRvzExtensions();

            var rvzEntry = archive.Entries.FirstOrDefault(e =>
                e is { IsDirectory: false, Key: not null } &&
                rvzExtensions.Any(ext => e.Key.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));

            var entry = rvzEntry ?? archive.Entries.FirstOrDefault(e =>
                e is { IsDirectory: false, Key: not null } &&
                supportedExtensions.Any(ext => e.Key.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));

            if (entry == null)
            {
                ProcessHelper.TryDeleteDirectory(tempDir);
                var archiveName = Path.GetFileName(archivePath);
                return (false, string.Empty, string.Empty, $"No supported disc image found inside {archiveName}.",
                    false);
            }

            var isRvzFile = rvzExtensions.Any(ext =>
                entry.Key?.EndsWith(ext, StringComparison.OrdinalIgnoreCase) == true);

            var entryName = entry.Key?.Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            entryName = Path.GetFileName(entryName);

            if (string.IsNullOrWhiteSpace(entryName))
            {
                var archiveName = Path.GetFileNameWithoutExtension(archivePath);
                var entryExtension = supportedExtensions.FirstOrDefault(ext =>
                    entry.Key?.EndsWith(ext, StringComparison.OrdinalIgnoreCase) == true) ?? ".iso";
                entryName = $"{archiveName}{entryExtension}";
            }

            var extractedFilePath = Path.Combine(tempDir, entryName);

            await using (var source = await entry.OpenEntryStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = File.Create(extractedFilePath))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            if (isRvzFile)
            {
                _logger.Information("{Message:l}", $"Extracted RVZ file {entryName} from archive.");
            }
            else
            {
                _logger.Information("{Message:l}", $"Extracted {entryName} from archive.");
            }

            return (true, extractedFilePath, tempDir, string.Empty, isRvzFile);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ProcessHelper.TryDeleteDirectory(tempDir);
            throw;
        }
        catch (Exception ex)
        {
            var archiveName = Path.GetFileName(archivePath);

            if (ex is OperationCanceledException)
            {
                _logger.Information("{Message:l}",
                    $"SharpCompress extraction failed for {archiveName} (internal cancellation), falling back to 7za.exe...");
            }
            else
            {
                _logger.Information("{Message:l}",
                    $"SharpCompress extraction failed for {archiveName}: {ex.Message}, falling back to 7za.exe...");
            }

            ProcessHelper.TryDeleteDirectory(tempDir);

            var sevenZipResult = await ExtractWith7ZipAsync(archivePath, cancellationToken);
            if (sevenZipResult.Success)
            {
                return sevenZipResult;
            }

            _logger.Information("{Message:l}",
                $"Extraction failed with both SharpCompress and 7za.exe for {archiveName}. File may be corrupt.");

            return (false, string.Empty, string.Empty,
                $"Failed to extract archive (file may be corrupt): {archiveName}", false);
        }
    }

    private async Task<(bool Success, string FilePath, string TempDir, string ErrorMessage, bool IsRvzFile)>
        ExtractWith7ZipAsync(string archivePath, CancellationToken cancellationToken)
    {
        var tempDir = string.Empty;
        Process? process = null;

        try
        {
            var sevenZipPath = ProcessHelper.Get7ZipExecutablePath();
            if (!File.Exists(sevenZipPath))
            {
                _logger.Information("{Message:l}", $"7za executable not found at: {sevenZipPath}");
                return (false, string.Empty, string.Empty, "7za executable not found.", false);
            }

            ProcessHelper.EnsureExecutable(sevenZipPath);

            tempDir = Path.Combine(Path.GetTempPath(), "RVZStudio_7Zip_" + Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);

            _logger.Information("{Message:l}", $"Extracting with 7za.exe to: {tempDir}");

            process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = sevenZipPath,
                Arguments = $"x -o\"{tempDir}\" -y \"{archivePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null) outputBuilder.AppendLine(args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null) errorBuilder.AppendLine(args.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);

            // WaitForExitAsync does not flush the asynchronous output handlers; the synchronous
            // wait guarantees the captured output is complete.
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                var errorOutput = errorBuilder.ToString();
                _logger.Information("{Message:l}",
                    $"7za.exe extraction failed with exit code {process.ExitCode}: {errorOutput}");
                ProcessHelper.TryDeleteDirectory(tempDir);
                return (false, string.Empty, string.Empty, $"7za.exe extraction failed: {errorOutput}", false);
            }

            var supportedExtensions = FileService.GetPrimaryTargetExtensionsInsideArchive();
            var rvzExtensions = FileService.GetRvzExtensions();

            var extractedFile = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return supportedExtensions.Contains(ext) || rvzExtensions.Contains(ext);
                });

            if (extractedFile is null)
            {
                _logger.Information("{Message:l}", "No supported disc image found in 7za.exe extraction output.");
                ProcessHelper.TryDeleteDirectory(tempDir);
                return (false, string.Empty, string.Empty, "No supported disc image found after 7za.exe extraction.",
                    false);
            }

            var isRvzFile = rvzExtensions.Contains(Path.GetExtension(extractedFile).ToLowerInvariant());
            var entryName = Path.GetFileName(extractedFile);

            if (isRvzFile)
            {
                _logger.Information("{Message:l}", $"Extracted RVZ file {entryName} from archive using 7za.exe.");
            }
            else
            {
                _logger.Information("{Message:l}", $"Extracted {entryName} from archive using 7za.exe.");
            }

            return (true, extractedFile, tempDir, string.Empty, isRvzFile);
        }
        catch (OperationCanceledException)
        {
            ProcessHelper.TryKillProcess(process);
            ProcessHelper.TryDeleteDirectory(tempDir);
            throw;
        }
        catch (Exception ex)
        {
            ProcessHelper.TryKillProcess(process);
            _logger.Information("{Message:l}", $"7za.exe extraction error: {ex.Message}");
            ProcessHelper.TryDeleteDirectory(tempDir);
            return (false, string.Empty, string.Empty, $"7za.exe extraction error: {ex.Message}", false);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private Task<bool> TryDeleteFileAsync(string filePath, string description)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.Information("{Message:l}", $"Deleted {description}: {Path.GetFileName(filePath)}");
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            _logger.Information("{Message:l}",
                $"Failed to delete {description} {Path.GetFileName(filePath)}: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    private Task TryDeleteDirectoryAsync(string dirPath, string description)
    {
        try
        {
            if (Directory.Exists(dirPath))
            {
                Directory.Delete(dirPath, true);
                _logger.Information("{Message:l}", $"Deleted {description}");
            }
        }
        catch (Exception ex)
        {
            _logger.Information("{Message:l}", $"Failed to delete {description}: {ex.Message}");
        }

        return Task.CompletedTask;
    }
}

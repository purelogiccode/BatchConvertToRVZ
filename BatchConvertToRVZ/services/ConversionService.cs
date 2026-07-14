using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Serilog;
using SharpCompress.Archives;

namespace BatchConvertToRVZ.services;

public class ConversionService
{
    private readonly ILogger _logger;
    private readonly FileService _fileService;

    public ConversionService(ILogger logger, FileService fileService)
    {
        _logger = logger.ForContext<ConversionService>();
        _fileService = fileService;
    }

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
        CancellationToken cancellationToken)
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

            var filesProcessedCount = 0;

            _logger.Information("{Message:l}", "Processing files sequentially.");
            foreach (var inputFile in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileName = Path.GetFileName(inputFile);
                _logger.Information("{Message:l}", $"Processing: {fileName}");

                var success = await ProcessFileAsync(
                    dolphinToolPath,
                    inputFile,
                    outputFolder,
                    deleteFiles,
                    compressionMethod,
                    compressionLevel,
                    blockSize,
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
                updateProgress(filesProcessedCount, totalFilesToProcess, fileName);
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
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(inputFile);
        var inputExtension = Path.GetExtension(inputFile).ToLowerInvariant();

        try
        {
            if (_fileService.GetArchiveExtensions().Contains(inputExtension))
            {
                return await ProcessArchiveFileAsync(
                    dolphinToolPath,
                    inputFile,
                    outputFolder,
                    deleteOriginal,
                    compressionMethod,
                    compressionLevel,
                    blockSize,
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
        CancellationToken cancellationToken)
    {
        var archiveFileName = Path.GetFileName(archivePath);

        try
        {
            _logger.Information("{Message:l}", $"Extracting archive: {archiveFileName}");

            var extractionResult = await ExtractArchiveAsync(archivePath, cancellationToken);
            if (!extractionResult.Success)
            {
                _logger.Information("{Message:l}", $"Failed to extract {archiveFileName}: {extractionResult.ErrorMessage}");
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
                        _logger.Information("{Message:l}", $"Error copying RVZ file from archive {fileName}: {ex.Message}");
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
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(inputFile);
        var outputFileName = _fileService.GetBaseFileNameWithoutGameExtension(fileName) + ".rvz";
        var outputFile = Path.Combine(outputFolder, outputFileName);

        try
        {
            if (_fileService.IsRvzFile(inputFile))
            {
                _logger.Information("{Message:l}", $"File is already in RVZ format, copying: {fileName} -> {outputFileName}");

                try
                {
                    Directory.CreateDirectory(outputFolder);
                    File.Copy(inputFile, outputFile, true);
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
        CancellationToken cancellationToken)
    {
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

            var outputChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait
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
                _logger.Information("{Message:l}", $"Conversion failed for {Path.GetFileName(inputFile)}. Exit code: {process.ExitCode}");
                _logger.Information("{Message:l}", $"Output: {output}");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); }
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

    private async Task<(bool Success, string FilePath, string TempDir, string ErrorMessage, bool IsRvzFile)> ExtractArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        var tempDir = string.Empty;

        try
        {
            tempDir = Path.Combine(Path.GetTempPath(), "BatchConvertToRVZ_Extract_" + Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);

            _logger.Information("{Message:l}", $"Extracting archive to temporary directory: {tempDir}");

            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var supportedExtensions = _fileService.GetPrimaryTargetExtensionsInsideArchive();
            var rvzExtensions = _fileService.GetRvzExtensions();

            var rvzEntry = archive.Entries.FirstOrDefault(e =>
                e is { IsDirectory: false, Key: not null } &&
                rvzExtensions.Any(ext => e.Key.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));

            var entry = rvzEntry ?? archive.Entries.FirstOrDefault(e =>
                e is { IsDirectory: false, Key: not null } &&
                supportedExtensions.Any(ext => e.Key.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));

            if (entry == null)
            {
                var archiveName = Path.GetFileName(archivePath);
                return (false, string.Empty, string.Empty, $"No supported disc image found inside {archiveName}.", false);
            }

            var isRvzFile = rvzExtensions.Any(ext =>
                entry.Key?.EndsWith(ext, StringComparison.OrdinalIgnoreCase) == true);

            var entryName = entry.Key?.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
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
            throw;
        }
        catch (Exception ex)
        {
            var archiveName = Path.GetFileName(archivePath);

            if (ex is OperationCanceledException)
            {
                _logger.Information("{Message:l}", $"SharpCompress extraction failed for {archiveName} (internal cancellation), falling back to 7za.exe...");
            }
            else
            {
                _logger.Information("{Message:l}", $"SharpCompress extraction failed for {archiveName}: {ex.Message}, falling back to 7za.exe...");
            }

            if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            var sevenZipResult = await ExtractWith7ZipAsync(archivePath, cancellationToken);
            if (sevenZipResult.Success)
            {
                return sevenZipResult;
            }

            _logger.Information("{Message:l}", $"Extraction failed with both SharpCompress and 7za.exe for {archiveName}. File may be corrupt.");

            return (false, string.Empty, string.Empty, $"Failed to extract archive (file may be corrupt): {archiveName}", false);
        }
    }

    private async Task<(bool Success, string FilePath, string TempDir, string ErrorMessage, bool IsRvzFile)> ExtractWith7ZipAsync(string archivePath, CancellationToken cancellationToken)
    {
        var tempDir = string.Empty;

        try
        {
            tempDir = Path.Combine(Path.GetTempPath(), "BatchConvertToRVZ_7Zip_" + Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);

            var sevenZipPath = Get7ZipExecutablePath();
            if (!File.Exists(sevenZipPath))
            {
                _logger.Information("{Message:l}", $"7za executable not found at: {sevenZipPath}");
                return (false, string.Empty, string.Empty, "7za executable not found.", false);
            }

            _logger.Information("{Message:l}", $"Extracting with 7za.exe to: {tempDir}");

            using var process = new Process();
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

            if (process.ExitCode != 0)
            {
                var errorOutput = errorBuilder.ToString();
                _logger.Information("{Message:l}", $"7za.exe extraction failed with exit code {process.ExitCode}: {errorOutput}");
                return (false, string.Empty, string.Empty, $"7za.exe extraction failed: {errorOutput}", false);
            }

            var supportedExtensions = _fileService.GetPrimaryTargetExtensionsInsideArchive();
            var rvzExtensions = _fileService.GetRvzExtensions();

            var extractedFile = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return supportedExtensions.Contains(ext) || rvzExtensions.Contains(ext);
                });

            if (extractedFile is null)
            {
                _logger.Information("{Message:l}", "No supported disc image found in 7za.exe extraction output.");
                return (false, string.Empty, string.Empty, "No supported disc image found after 7za.exe extraction.", false);
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
            throw;
        }
        catch (Exception ex)
        {
            _logger.Information("{Message:l}", $"7za.exe extraction error: {ex.Message}");

            if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            return (false, string.Empty, string.Empty, $"7za.exe extraction error: {ex.Message}", false);
        }
    }

    internal static string Get7ZipExecutablePath()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        var exeName = architecture switch
        {
            Architecture.Arm64 => "7za_arm64.exe",
            _ => "7za.exe"
        };

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exeName);
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
            _logger.Information("{Message:l}", $"Failed to delete {description} {Path.GetFileName(filePath)}: {ex.Message}");
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

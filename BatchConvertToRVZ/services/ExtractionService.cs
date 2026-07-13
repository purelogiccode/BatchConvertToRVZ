using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;
using SharpCompress.Archives;

namespace BatchConvertToRVZ.services;

public class ExtractionService
{
    private readonly ILogger _logger;
    private readonly FileService _fileService;

    private static readonly HashSet<string> ValidOutputFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "iso",
        "wbfs",
        "gcz",
        "wia"
    };

    public ExtractionService(ILogger logger, FileService fileService)
    {
        _logger = logger.ForContext<ExtractionService>();
        _fileService = fileService;
    }

    public async Task PerformBatchExtractionAsync(
        string dolphinToolPath,
        string[] files,
        string outputFolder,
        bool deleteFiles,
        string outputFormat,
        Action<int, int, string> updateProgress,
        Action<int> incrementSuccess,
        Action<int> incrementFailure,
        CancellationToken cancellationToken)
    {
        if (!ValidOutputFormats.Contains(outputFormat))
        {
            _logger.Information("{Message:l}", $"Error: Invalid output format '{outputFormat}'. Valid formats are: {string.Join(", ", ValidOutputFormats)}");
            return;
        }

        try
        {
            _logger.Information("{Message:l}", "Preparing for batch extraction...");

            var totalFilesToProcess = files.Length;
            _logger.Information("{Message:l}", $"Processing {totalFilesToProcess} selected files.");

            if (totalFilesToProcess == 0)
            {
                _logger.Information("{Message:l}", "No files selected for extraction.");
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
                    outputFormat,
                    cancellationToken);

                if (success)
                {
                    incrementSuccess(1);
                    _logger.Information("{Message:l}", $"Extraction successful: {fileName}");
                }
                else
                {
                    incrementFailure(1);
                    _logger.Information("{Message:l}", $"Extraction failed: {fileName}");
                }

                filesProcessedCount++;
                updateProgress(filesProcessedCount, totalFilesToProcess, fileName);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("{Message:l}", "Batch extraction operation was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error during batch extraction operation: {Message}", ex.Message);
        }
    }

    private async Task<bool> ProcessFileAsync(
        string dolphinToolPath,
        string inputFile,
        string outputFolder,
        bool deleteOriginal,
        string outputFormat,
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
                    outputFormat,
                    cancellationToken);
            }
            else
            {
                return await ExtractSingleFileAsync(
                    dolphinToolPath,
                    inputFile,
                    outputFolder,
                    deleteOriginal,
                    outputFormat,
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
        string outputFormat,
        CancellationToken cancellationToken)
    {
        var archiveFileName = Path.GetFileName(archivePath);

        try
        {
            _logger.Information("{Message:l}", $"Extracting archive: {archiveFileName}");

            var extractionResult = await ExtractRvzFromArchiveAsync(archivePath, cancellationToken);
            if (!extractionResult.Success)
            {
                _logger.Information("{Message:l}", $"Failed to extract {archiveFileName}: {extractionResult.ErrorMessage}");
                return false;
            }

            var extractedFilePath = extractionResult.FilePath;
            var tempDir = extractionResult.TempDir;

            try
            {
                var success = await ExtractSingleFileAsync(
                    dolphinToolPath,
                    extractedFilePath,
                    outputFolder,
                    false,
                    outputFormat,
                    cancellationToken);

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

    private async Task<(bool Success, string FilePath, string TempDir, string ErrorMessage)> ExtractRvzFromArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        var tempDir = string.Empty;

        try
        {
            tempDir = Path.Combine(Path.GetTempPath(), "BatchConvertToRVZ_Extract_" + Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);

            _logger.Information("{Message:l}", $"Extracting archive to temporary directory: {tempDir}");

            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var rvzExtensions = _fileService.GetRvzExtensions();

            var entry = archive.Entries.FirstOrDefault(e =>
                e is { IsDirectory: false, Key: not null } &&
                rvzExtensions.Any(ext => e.Key.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));

            if (entry == null)
            {
                var archiveName = Path.GetFileName(archivePath);
                return (false, string.Empty, string.Empty, $"No RVZ file found inside {archiveName}.");
            }

            var entryName = entry.Key?.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            entryName = Path.GetFileName(entryName);

            if (string.IsNullOrWhiteSpace(entryName))
            {
                var archiveName = Path.GetFileNameWithoutExtension(archivePath);
                var entryExtension = rvzExtensions.FirstOrDefault(ext =>
                    entry.Key?.EndsWith(ext, StringComparison.OrdinalIgnoreCase) == true) ?? ".rvz";
                entryName = $"{archiveName}{entryExtension}";
            }

            var extractedFilePath = Path.Combine(tempDir, entryName);

            await using (var source = await entry.OpenEntryStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = File.Create(extractedFilePath))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            _logger.Information("{Message:l}", $"Extracted {entryName} from archive.");
            return (true, extractedFilePath, tempDir, string.Empty);
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

            var sevenZipResult = await ExtractRvzWith7ZipAsync(archivePath, cancellationToken);
            if (sevenZipResult.Success)
            {
                return sevenZipResult;
            }

            _logger.Information("{Message:l}", $"Extraction failed with both SharpCompress and 7za.exe for {archiveName}. File may be corrupt.");

            return (false, string.Empty, string.Empty, $"Failed to extract archive (file may be corrupt): {archiveName}");
        }
    }

    private async Task<(bool Success, string FilePath, string TempDir, string ErrorMessage)> ExtractRvzWith7ZipAsync(string archivePath, CancellationToken cancellationToken)
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
                return (false, string.Empty, string.Empty, "7za executable not found.");
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
                return (false, string.Empty, string.Empty, $"7za.exe extraction failed: {errorOutput}");
            }

            var rvzExtensions = _fileService.GetRvzExtensions();

            var extractedFile = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return rvzExtensions.Contains(ext);
                });

            if (extractedFile is null)
            {
                _logger.Information("{Message:l}", "No RVZ file found in 7za.exe extraction output.");
                return (false, string.Empty, string.Empty, "No RVZ file found after 7za.exe extraction.");
            }

            var entryName = Path.GetFileName(extractedFile);
            _logger.Information("{Message:l}", $"Extracted {entryName} from archive using 7za.exe.");

            return (true, extractedFile, tempDir, string.Empty);
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

            return (false, string.Empty, string.Empty, $"7za.exe extraction error: {ex.Message}");
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

    private async Task<bool> ExtractSingleFileAsync(
        string dolphinToolPath,
        string inputFile,
        string outputFolder,
        bool deleteOriginal,
        string outputFormat,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(inputFile);
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var outputFileName = fileNameWithoutExt + "." + outputFormat.ToLowerInvariant();
        var outputFile = Path.Combine(outputFolder, outputFileName);

        try
        {
            _logger.Information("{Message:l}", $"Converting to {outputFormat.ToUpperInvariant()}: {fileName} -> {outputFileName}");

            if (File.Exists(outputFile))
            {
                try
                {
                    File.Delete(outputFile);
                    _logger.Information("{Message:l}", $"Deleted existing output file: {outputFileName}");
                }
                catch (Exception ex)
                {
                    _logger.Information("{Message:l}", $"Failed to delete existing file {outputFileName}: {ex.Message}");
                    return false;
                }
            }

            var success = await ConvertToFormatAsync(
                dolphinToolPath,
                inputFile,
                outputFile,
                outputFormat,
                cancellationToken);

            if (success && deleteOriginal)
            {
                await TryDeleteFileAsync(inputFile, "original file");
            }

            return success;
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

    private async Task<bool> ConvertToFormatAsync(
        string dolphinToolPath,
        string inputFile,
        string outputFile,
        string outputFormat,
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
            process.StartInfo.ArgumentList.Add(outputFormat.ToLowerInvariant());

            process.EnableRaisingEvents = true;

            var outputQueue = new ConcurrentQueue<string>();
            var errorQueue = new ConcurrentQueue<string>();
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
                    outputQueue.Enqueue(args.Data);
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
                    errorQueue.Enqueue(args.Data);
                    _logger.Information("{Message:l}", $"[DolphinTool ERROR] {args.Data}");
                }
            };

            ProcessHelper.SuppressErrorDialogs();
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputCompleted.Task, errorCompleted.Task);

            var outputBuilder = new StringBuilder();
            while (outputQueue.TryDequeue(out var line)) outputBuilder.AppendLine(line);
            var output = outputBuilder.ToString();

            var errorBuilder = new StringBuilder();
            while (errorQueue.TryDequeue(out var line)) errorBuilder.AppendLine(line);
            var error = errorBuilder.ToString();

            if (process.ExitCode == 0)
            {
                var fileExists = await WaitForFileExistsAsync(outputFile, cancellationToken);

                if (fileExists)
                {
                    _logger.Information("{Message:l}", $"Successfully converted to {outputFormat.ToUpperInvariant()}: {Path.GetFileName(outputFile)}");
                    return true;
                }
                else
                {
                    _logger.Information("{Message:l}", $"Conversion failed for {Path.GetFileName(inputFile)}. Output file not found after waiting.");
                    if (!string.IsNullOrEmpty(error))
                        _logger.Information("{Message:l}", $"Error output: {error}");
                    return false;
                }
            }
            else
            {
                _logger.Information("{Message:l}", $"Conversion failed for {Path.GetFileName(inputFile)}. Exit code: {process.ExitCode}");
                if (!string.IsNullOrEmpty(output))
                    _logger.Information("{Message:l}", $"Output: {output}");
                if (!string.IsNullOrEmpty(error))
                    _logger.Information("{Message:l}", $"Error output: {error}");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
        catch (Exception ex)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    /* ignore */
                }
            }

            _logger.Information("{Message:l}", $"DolphinTool process error: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> WaitForFileExistsAsync(string filePath, CancellationToken cancellationToken)
    {
        const int maxAttempts = 50;
        const int delayMs = 100;

        for (var i = 0; i < maxAttempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(filePath))
                return true;

            await Task.Delay(delayMs, cancellationToken);
        }

        return false;
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

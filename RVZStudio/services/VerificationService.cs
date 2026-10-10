using System.Diagnostics;
using System.Globalization;
using System.Text;
using Serilog;

namespace RVZStudio.services;

/// <summary>
/// Verifies the integrity of RVZ/WIA disc images. RVZSharp is the primary engine (Dolphin's
/// volume verifier); DolphinTool is used as a fallback when the native engine cannot verify.
/// </summary>
public class VerificationService
{
    private readonly ILogger _logger;
    private readonly RvzSharpService _rvzSharpService;

    /// <summary>
    /// Initializes a new instance of the <see cref="VerificationService"/> class.
    /// </summary>
    /// <param name="logger">The logger used to report verification activity and failures.</param>
    /// <param name="rvzSharpService">The native engine, or null to create a new one.</param>
    public VerificationService(ILogger logger, RvzSharpService? rvzSharpService = null)
    {
        _logger = logger.ForContext<VerificationService>();
        _rvzSharpService = rvzSharpService ?? new RvzSharpService(logger);
    }

    /// <summary>
    /// Verifies a batch of RVZ files, reporting progress and per-file success/failure.
    /// </summary>
    /// <param name="dolphinToolPath">Path to the optional DolphinTool fallback executable.</param>
    /// <param name="files">The RVZ file paths to verify.</param>
    /// <param name="moveFailed">Whether to move failed files into a "_Failed" subfolder.</param>
    /// <param name="moveSuccess">Whether to move successful files into a "_Success" subfolder.</param>
    /// <param name="updateProgress">Receives (processed, total, currentFileName) after each file.</param>
    /// <param name="incrementSuccess">Called with the number of newly succeeded files.</param>
    /// <param name="incrementFailure">Called with the number of newly failed files.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <param name="fileProgress">Optional per-file progress receiver (fraction in [0, 1]).</param>
    public async Task PerformBatchVerificationAsync(
        string dolphinToolPath,
        string[] files,
        bool moveFailed,
        bool moveSuccess,
        Action<int, int, string> updateProgress,
        Action<int> incrementSuccess,
        Action<int> incrementFailure,
        CancellationToken cancellationToken,
        IProgress<double>? fileProgress = null)
    {
        try
        {
            _logger.Information("{Message:l}", "Preparing for batch verification...");

            var totalFilesToProcess = files.Length;
            _logger.Information("{Message:l}", $"Verifying {totalFilesToProcess} selected RVZ files.");

            if (totalFilesToProcess == 0)
            {
                _logger.Information("{Message:l}", "No files selected for verification.");
                return;
            }

            var filesProcessedCount = 0;

            _logger.Information("{Message:l}", "Verifying files sequentially.");
            foreach (var inputFile in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileName = Path.GetFileName(inputFile);
                var baseFolder = Path.GetDirectoryName(inputFile) ?? string.Empty;

                fileProgress?.Report(0);

                var success = await VerifyRvzFileAsync(
                    dolphinToolPath,
                    inputFile,
                    baseFolder,
                    moveFailed,
                    moveSuccess,
                    fileProgress,
                    cancellationToken);

                if (success)
                {
                    incrementSuccess(1);
                }
                else
                {
                    incrementFailure(1);
                }

                filesProcessedCount++;
                updateProgress(filesProcessedCount, totalFilesToProcess, fileName);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("{Message:l}", "Batch verification operation was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error during batch verification operation: {Message}", ex.Message);
        }
    }

    private async Task<bool> VerifyRvzFileAsync(
        string dolphinToolPath,
        string inputFile,
        string baseFolder,
        bool moveFailed,
        bool moveSuccess,
        IProgress<double>? fileProgress,
        CancellationToken token)
    {
        var fileName = Path.GetFileName(inputFile);
        _logger.Information("{Message:l}", $"Verifying: {fileName}...");

        // RVZSharp is the primary engine: it verifies the container and, for Wii discs,
        // walks every partition's hash tree like Dolphin's volume verifier.
        if (RvzSharpService.CanDecode(inputFile))
        {
            var nativeResult = await Task.Run(
                () => _rvzSharpService.TryVerify(inputFile, fileProgress, token), token).ConfigureAwait(false);

            if (nativeResult is not null)
            {
                var nativeSuccess = nativeResult.Value;
                if (nativeSuccess)
                {
                    _logger.Information("{Message:l}", $"Verification successful: {fileName}");

                    if (moveSuccess)
                    {
                        await MoveFileToSubfolderAsync(inputFile, baseFolder, "_Success", token);
                    }
                }
                else
                {
                    _logger.Information("{Message:l}", $"Verification failed: {fileName}");

                    if (moveFailed)
                    {
                        await MoveFileToSubfolderAsync(inputFile, baseFolder, "_Failed", token);
                    }
                }

                return nativeSuccess;
            }

            _logger.Information("{Message:l}",
                $"RVZSharp could not verify {fileName}; falling back to DolphinTool.");
        }

        using var process = new Process();
        var verificationResult = false;
        string? tempWorkingDirectory = null;

        DataReceivedEventHandler? outputHandler = null;
        DataReceivedEventHandler? errorHandler = null;

        try
        {
            if (!ProcessHelper.ExecutableExists(dolphinToolPath))
            {
                // DolphinTool is an optional fallback; a missing helper is an expected environment
                // condition, so log it at Information level (no bug report) and fail this file.
                _logger.Information("{Message:l}", ProcessHelper.GetMissingExecutableMessage(dolphinToolPath));
                return false;
            }

            ProcessHelper.EnsureExecutable(dolphinToolPath);

            tempWorkingDirectory = Path.Combine(Path.GetTempPath(),
                "RVZStudio_DolphinTool_Temp_" + Path.GetRandomFileName());
            Directory.CreateDirectory(tempWorkingDirectory);

            process.StartInfo = new ProcessStartInfo
            {
                FileName = dolphinToolPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = tempWorkingDirectory
            };
            process.StartInfo.ArgumentList.Add("verify");
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(inputFile);

            process.EnableRaisingEvents = true;

            var outputQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var outputCompleted = new TaskCompletionSource<bool>();
            var errorCompleted = new TaskCompletionSource<bool>();

            outputHandler = (_, args) =>
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
            errorHandler = (_, args) =>
            {
                if (args.Data is null)
                {
                    errorCompleted.TrySetResult(true);
                }
                else
                {
                    outputQueue.Enqueue(args.Data);
                    _logger.Information("{Message:l}", $"[DolphinTool ERROR] {args.Data}");
                }
            };
            process.OutputDataReceived += outputHandler;
            process.ErrorDataReceived += errorHandler;

            ProcessHelper.SuppressErrorDialogs();
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(token);
            await Task.WhenAll(outputCompleted.Task, errorCompleted.Task);

            var outputBuilder = new StringBuilder();
            while (outputQueue.TryDequeue(out var line)) outputBuilder.AppendLine(line);
            var output = outputBuilder.ToString();

            if (process.ExitCode == 0 && output.Contains("Problems Found: No"))
            {
                verificationResult = true;
                _logger.Information("{Message:l}", $"Verification successful: {fileName}");

                if (moveSuccess)
                {
                    await MoveFileToSubfolderAsync(inputFile, baseFolder, "_Success", token);
                }
            }
            else
            {
                _logger.Information("{Message:l}", $"Verification failed: {fileName}");
                _logger.Information("{Message:l}", $"Exit code: {process.ExitCode}");
                _logger.Information("{Message:l}", $"Output: {output}");

                if (moveFailed)
                {
                    await MoveFileToSubfolderAsync(inputFile, baseFolder, "_Failed", token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("{Message:l}", $"Verification canceled: {fileName}");
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch
            {
                // The process may not have started (or already exited): HasExited/Kill
                // throw InvalidOperationException when no process is associated.
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error verifying file {FileName}: {Message}", fileName, ex.Message);
            verificationResult = false;
        }
        finally
        {
            if (outputHandler != null)
            {
                process.OutputDataReceived -= outputHandler;
            }

            if (errorHandler != null)
            {
                process.ErrorDataReceived -= errorHandler;
            }

            if (tempWorkingDirectory != null)
            {
                // Cleanup must not depend on the operation token: when the user cancels,
                // the token is already canceled and the task would never run, leaking the folder.
                _ = DeleteDirectoryAsync(tempWorkingDirectory);
            }
        }

        return verificationResult;
    }

    private async Task MoveFileToSubfolderAsync(string sourceFilePath, string baseFolder, string subfolderName,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(sourceFilePath);
            var subfolderPath = Path.Combine(baseFolder, subfolderName);

            if (!Directory.Exists(subfolderPath))
            {
                Directory.CreateDirectory(subfolderPath);
            }

            var destinationPath = Path.Combine(subfolderPath, fileName);

            if (File.Exists(destinationPath))
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                var extension = Path.GetExtension(fileName);
                destinationPath = Path.Combine(subfolderPath, $"{nameWithoutExt}_{timestamp}{extension}");
            }

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(sourceFilePath, destinationPath);
            }, cancellationToken);

            _logger.Information("{Message:l}", $"Moved {fileName} to {subfolderName} folder.");
        }
        catch (OperationCanceledException)
        {
            _logger.Information("{Message:l}", $"Move file operation cancelled for {Path.GetFileName(sourceFilePath)}");
            throw;
        }
        catch (Exception ex)
        {
            // A real move failure (locked file, access denied) must be visible and reported
            // to the Bug Report API instead of looking like a success.
            _logger.Warning(ex, "Failed to move file to {SubfolderName} folder", subfolderName);
        }
    }

    private static async Task DeleteDirectoryAsync(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                await Task.Run(() => Directory.Delete(path, true));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Cleanup is best-effort only.
            Log.Debug(ex, "Failed to delete temporary directory {Path}", path);
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Serilog;

namespace BatchConvertToRVZ.services;

public class VerificationService
{
    private readonly ILogger _logger;

    public VerificationService(ILogger logger)
    {
        _logger = logger.ForContext<VerificationService>();
    }

    public async Task PerformBatchVerificationAsync(
        string dolphinToolPath,
        string[] files,
        bool moveFailed,
        bool moveSuccess,
        Action<int, int, string> updateProgress,
        Action<int> incrementSuccess,
        Action<int> incrementFailure,
        CancellationToken cancellationToken)
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
                var success = await VerifyRvzFileAsync(
                    dolphinToolPath,
                    inputFile,
                    baseFolder,
                    moveFailed,
                    moveSuccess,
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
        CancellationToken token)
    {
        var fileName = Path.GetFileName(inputFile);
        using var process = new Process();
        var verificationResult = false;
        string? tempWorkingDirectory = null;

        DataReceivedEventHandler? outputHandler = null;
        DataReceivedEventHandler? errorHandler = null;

        try
        {
            _logger.Information("{Message:l}", $"Verifying: {fileName}...");

            tempWorkingDirectory = Path.Combine(Path.GetTempPath(), "BatchConvertToRVZ_DolphinTool_Temp_" + Path.GetRandomFileName());
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
            if (!process.HasExited)
            {
                process.Kill(true);
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
                _ = Task.Run(async () => await DeleteDirectoryAsync(tempWorkingDirectory), token);
            }
        }

        return verificationResult;
    }

    private async Task MoveFileToSubfolderAsync(string sourceFilePath, string baseFolder, string subfolderName, CancellationToken cancellationToken)
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
            _logger.Information("{Message:l}", $"Failed to move file to {subfolderName} folder: {ex.Message}");
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
        catch (Exception)
        {
            // Silently ignore cleanup errors
        }
    }
}

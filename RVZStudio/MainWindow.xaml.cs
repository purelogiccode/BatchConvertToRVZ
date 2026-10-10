using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RVZStudio.dialogs;
using RVZStudio.Models;
using RVZStudio.services;
using Serilog;
using IDisposable = System.IDisposable;

namespace RVZStudio;

/// <summary>
/// Interaction logic for the MainWindow.
/// Provides batch conversion and verification of game disc images to RVZ format.
/// </summary>
public partial class MainWindow : Window, IDisposable
{
    private bool _disposed;
    private volatile bool _isClosing;
    private volatile bool _isShuttingDown;
    private Task? _runningTask;
    private string? _dolphinToolPath;
    private CancellationTokenSource _cts;
    private readonly Lock _ctsLock = new();
    private readonly Lock _closingLock = new();
    private readonly UpdateService _updateService;
    private readonly ConversionService _conversionService;
    private readonly VerificationService _verificationService;
    private readonly ExtractionService _extractionService;
    private readonly ScreenshotService _screenshotService;
    private readonly DiscExplorerService _discExplorerService;
    private DiscExplorerSession? _explorerSession;
    private readonly ObservableCollection<ExplorerTreeNode> _explorerRoots = new();
    private bool _isExplorerBusy;

    private const string GitHubApiUrl = "https://api.github.com/repos/purelogiccode/BatchConvertToRVZ/releases/latest";

    // Compression settings (now instance variables to allow user configuration)
    private string _rvzCompressionMethod = "zstd"; // Default compression method
    private int _rvzCompressionLevel = 5; // Default compression level
    private int _rvzBlockSize = 131072; // Default block size (128KB)

    // Compression level ranges for different methods
    private static readonly Dictionary<string, (int Min, int Max)> CompressionLevelRanges =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "zstd", (1, 22) },
            { "zlib", (1, 9) },
            { "lzma", (1, 9) },
            { "lzma2", (1, 9) },
            { "bzip2", (1, 9) },
            { "lz4", (1, 12) }
        };

    // Extension arrays moved to FileService

    // Statistics
    private int _totalFilesToProcess;
    private int _successCount;
    private int _failureCount;
    private readonly Stopwatch _operationTimer = new();
    private DispatcherTimer? _processingTimeUpdateTimer;
    private readonly Lock _statsLock = new();

    // Write speed calculation
    private long _totalBytesProcessed;
    private DateTime _speedCalculationStartTime;
    private readonly Lock _speedLock = new();

    // Fields for verification move options
    private bool _moveFailedFiles;
    private bool _moveSuccessFiles;

    // Current operation type for proper cancellation messaging
    private OperationType _currentOperation = OperationType.None;

    // File lists for UI
    private readonly ObservableCollection<FileItem> _conversionFiles = new();
    private readonly ObservableCollection<FileItem> _verificationFiles = new();
    private readonly ObservableCollection<FileItem> _extractionFiles = new();

    private void UpdateOverallProgress()
    {
        try
        {
            int totalToProcess;
            int successCount;
            int failureCount;

            lock (_statsLock)
            {
                if (_totalFilesToProcess == 0) return;

                totalToProcess = _totalFilesToProcess;
                successCount = _successCount;
                failureCount = _failureCount;
            }

            Dispatcher.UIThread.Post(() =>
            {
                var completed = successCount + failureCount;
                ProgressBar.Value = Math.Min(completed, totalToProcess);
            });
        }
        catch (TaskCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
            // Dispatcher is shutting down
        }
    }

    private void UpdateStatusBar(string status)
    {
        try
        {
            Dispatcher.UIThread.Post(() => StatusBarText.Text = status);
        }
        catch (TaskCanceledException)
        {
            // Expected during application shutdown
        }
        catch (InvalidOperationException)
        {
            // Dispatcher is shutting down
        }
    }


    // Log batching
    private readonly Channel<string> _logChannel = Channel.CreateUnbounded<string>();
    private readonly Task? _logProcessorTask;
    private int _logLineCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// Sets up the UI, logging system, and checks for dependencies.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        _cts = new CancellationTokenSource();

        // Start log processor and subscribe the UI sink
        _logProcessorTask = Task.Run(ProcessLogsAsync);
        UiLogSink.MessageLogged += EnqueueLogLine;

        // The BugReportService is now initialized and managed by the App class.
        _updateService = new UpdateService(GitHubApiUrl);

        // Initialize service classes with Serilog ILogger (Warning+ auto-forwards to BugReport API)
        var rvzSharpService = new RvzSharpService(Log.Logger);
        _conversionService = new ConversionService(Log.Logger, rvzSharpService);
        _verificationService = new VerificationService(Log.Logger, rvzSharpService);
        _extractionService = new ExtractionService(Log.Logger, rvzSharpService);
        _screenshotService = new ScreenshotService(Log.Logger);
        _discExplorerService = new DiscExplorerService(Log.Logger);

        LogMessage("Welcome to RVZStudio.");
        LogMessage("");
        LogMessage("Use the 'Convert to RVZ' tab to convert ISO, GCM, WBFS, GCZ, WIA or NKIT.ISO files to RVZ.");
        LogMessage("Use the 'Verify Integrity of RVZ' tab to check the integrity of RVZ files.");
        LogMessage("Use the 'Extract from RVZ' tab to convert RVZ files to ISO, WBFS, GCZ or WIA format.");
        LogMessage("");
        LogMessage("");

        CheckDependencies();

        // Initialize DataGrids
        ConversionFilesDataGrid.ItemsSource = _conversionFiles;
        VerificationFilesDataGrid.ItemsSource = _verificationFiles;
        ExtractionFilesDataGrid.ItemsSource = _extractionFiles;
        ExplorerTreeView.ItemsSource = _explorerRoots;

        ResetOperationStats();
        InitializeProcessingTimeTimer();
        Loaded += MainWindow_LoadedAsync;
        Closed += MainWindow_Closed;
    }

    private void InitializeProcessingTimeTimer()
    {
        _processingTimeUpdateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _processingTimeUpdateTimer.Tick += (_, _) => UpdateProcessingTimeDisplay();
    }

    private void CheckDependencies()
    {
        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;

        // The native RVZSharp engine is built into the application, so no external
        // executable is strictly required; DolphinTool is an optional fallback.
        try
        {
            var dolphinToolExeName = GetDolphinToolExecutableName();
            _dolphinToolPath = Path.Combine(appDirectory, dolphinToolExeName);

            if (File.Exists(_dolphinToolPath))
            {
                LogMessage($"{dolphinToolExeName} found in the application directory.");
            }
            else
            {
                LogMessage(
                    $"WARNING: {dolphinToolExeName} was not found. The native RVZSharp engine handles all supported operations; the DolphinTool fallback is unavailable.");
            }
        }
        catch (PlatformNotSupportedException ex)
        {
            _dolphinToolPath = null;
            LogMessage(
                $"WARNING: Unsupported platform architecture for DolphinTool ({ex.Message}). The native RVZSharp engine will be used.");
        }

        LogMessage("SharpCompress library loaded for archive extraction.");
        LogMessage("");
    }

    /// <summary>
    /// Gets the DolphinTool executable file name for the current process architecture
    /// (with the "_arm64" suffix on ARM64 and the ".exe" extension on Windows).
    /// </summary>
    /// <returns>The architecture-specific DolphinTool file name.</returns>
    /// <exception cref="PlatformNotSupportedException">The architecture is neither x64 nor ARM64.</exception>
    internal static string GetDolphinToolExecutableName()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        var suffix = architecture switch
        {
            Architecture.X64 => string.Empty,
            Architecture.Arm64 => "_arm64",
            _ => throw new PlatformNotSupportedException($"Unsupported architecture: {architecture}")
        };

        // Windows releases ship DolphinTool(.exe); Linux/macOS builds use extension-less binaries.
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        return $"DolphinTool{suffix}{extension}";
    }

    private async void MainWindow_LoadedAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Check for updates on startup in the background
            await CheckForUpdatesAsync(false);
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync("Error during startup", ex);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        try
        {
            UiLogSink.MessageLogged -= EnqueueLogLine;
            _ = Task.Run(Dispose);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method MainWindow_Closed");
        }
    }

    private void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        lock (_closingLock)
        {
            if (_isClosing)
            {
                // Allow the close to proceed — this is triggered by Application shutdown
                // after a running operation was cancelled. Do NOT cancel here.
                return;
            }

            _isShuttingDown = true;
            _logChannel.Writer.TryComplete();

            if (_runningTask is not { IsCompleted: false })
            {
                // No operation running — allow close immediately
                return;
            }

            // An operation is running: cancel it and close once it stops
            e.Cancel = true;
            _isClosing = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                lock (_ctsLock)
                {
                    _cts.Cancel();
                }

                // Give the running operation time to stop and clean up its partial output.
                var completed = await Task.WhenAny(_runningTask!, Task.Delay(10000)) == _runningTask;
                if (!completed)
                {
                    Log.Information("Operation did not stop within 10 seconds; forcing application shutdown.");
                }
            }
            catch
            {
                // Ignore all errors during shutdown cancellation
            }
            finally
            {
                // Force-exit on the UI thread regardless of task state
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (Application.Current?.ApplicationLifetime is
                            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                        {
                            desktop.Shutdown();
                        }
                        else
                        {
                            Environment.Exit(0);
                        }
                    }
                    catch
                    {
                        // If Shutdown fails, fall back to Environment.Exit
                        Environment.Exit(0);
                    }
                });

                // Double-fallback: if Shutdown hasn't worked within 5 seconds, force-exit
                _ = Task.Delay(5000).ContinueWith(static _ => Environment.Exit(0));
            }
        });
    }

    private async void Window_KeyDownAsync(object? sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key != Key.F8) return;

            e.Handled = true;

            await _screenshotService.CaptureWindowAsync(this);
        }
        catch (Exception ex)
        {
            await ReportBugAsync("Error during Window_KeyDownAsync screenshot capture", ex);
        }
    }

    private const int MaxLogLines = 5000;

    /// <summary>
    /// Called by <see cref="UiLogSink"/> for every log event. Writes the pre-formatted
    /// line into the bounded channel so <see cref="ProcessLogsAsync"/> can batch it to the UI.
    /// </summary>
    private void EnqueueLogLine(object? sender, UiLogSink.LogMessageEventArgs e)
    {
        if (_disposed) return;

        try
        {
            _logChannel.Writer.TryWrite(e.Message);
        }
        catch (ChannelClosedException)
        {
            // Channel is closed, ignore
        }
    }

    /// <summary>
    /// Logs a message at Information level through Serilog.
    /// The message appears in the on-screen log viewer (via <see cref="UiLogSink"/>)
    /// and is written to the rolling daily log file. Information-level events are NOT
    /// forwarded to the Bug Report API.
    /// </summary>
    private void LogMessage(string message)
    {
        if (_disposed) return;

        Log.Information("{Message:l}", message);
    }

    private void AppendLogText(string text)
    {
        LogViewer.Text = string.Concat(LogViewer.Text, text);
        _logLineCount += text.Count(static c => c == '\n');
    }

    private void ClearLogViewer()
    {
        LogViewer.Text = string.Empty;
        _logLineCount = 0;
    }

    private async Task ProcessLogsAsync()
    {
        var batch = new List<string>();
        while (!_isShuttingDown && await _logChannel.Reader.WaitToReadAsync())
        {
            while (!_isShuttingDown && _logChannel.Reader.TryRead(out var log))
            {
                batch.Add(log);
                if (batch.Count >= 50) break; // Process in batches of 50
            }

            if (batch.Count > 0)
            {
                if (_isShuttingDown)
                {
                    // Skip UI updates during shutdown to prevent freeze
                    batch.Clear();
                    break;
                }
                else
                {
                    var combinedLogs = string.Join(Environment.NewLine, batch) + Environment.NewLine;
                    batch.Clear();

                    try
                    {
                        if (Application.Current is null) return;

                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (_disposed) return;

                            // Only scroll to end if the user is already at the bottom (or very close to it)
                            // This allows users to scroll up to read previous logs without being snapped back
                            var isAtBottom = LogViewerScrollViewer.Offset.Y + LogViewerScrollViewer.Viewport.Height >=
                                             LogViewerScrollViewer.Extent.Height - 10;

                            AppendLogText(combinedLogs);

                            // Efficiently clear log if it exceeds the limit to prevent UI freeze
                            if (_logLineCount > MaxLogLines)
                            {
                                ClearLogViewer();
                                AppendLogText(
                                    $"[{DateTime.Now:HH:mm:ss.fff}] --- Log cleared (exceeded {MaxLogLines} lines) to prevent UI freeze ---{Environment.NewLine}");
                                isAtBottom = true; // Always scroll to end after clear
                            }

                            if (isAtBottom)
                            {
                                LogViewerScrollViewer.ScrollToEnd();
                            }
                        }, DispatcherPriority.Background);
                    }
                    catch (Exception ex)
                    {
                        // Silently fail if the Dispatcher is shutting down.
                        // Log directly (not through the UI sink) to avoid re-entry into the
                        // channel that is already failing.
                        if (ex is not InvalidOperationException && ex is not TaskCanceledException)
                        {
                            Log.Error(ex, "Error in ProcessLogsAsync Dispatcher operation");
                        }
                    }
                }
            }

            // Small delay to allow batching more logs if they are arriving rapidly
            // Check shutdown flag before delay to exit quickly
            if (!_isShuttingDown)
            {
                await Task.Delay(100);
            }
        }
    }

    private async void BrowseInputButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var inputFolder = await SelectFolderAsync("Select the folder containing ISO files or archives to convert");
            if (string.IsNullOrEmpty(inputFolder)) return;

            InputFolderTextBox.Text = inputFolder;
            LogMessage($"Input folder selected: {inputFolder}");

            PopulateConversionFilesList(inputFolder);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method BrowseInputButton_ClickAsync");
        }
    }

    private void PopulateConversionFilesList(string inputFolder)
    {
        try
        {
            // Validate folder path before attempting to enumerate files
            if (string.IsNullOrWhiteSpace(inputFolder))
            {
                LogMessage("Error: Input folder path is empty.");
                return;
            }

            if (!Directory.Exists(inputFolder))
            {
                LogMessage($"Error: Input folder does not exist or is inaccessible: {inputFolder}");
                return;
            }

            _conversionFiles.Clear();

            var files = Directory.GetFiles(inputFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(file => FileService.IsSupportedInputFile(file))
                .ToArray();

            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                _conversionFiles.Add(new FileItem
                {
                    FileName = Path.GetFileName(file),
                    FullPath = file,
                    FileSize = fileInfo.Length,
                    IsSelected = true
                });
            }

            ConversionFilesDataGrid.ItemsSource = _conversionFiles;
            LogMessage($"Found {_conversionFiles.Count} files in input folder.");
        }
        catch (Exception ex)
        {
            LogMessage($"Error populating file list: {ex.Message}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await ReportBugAsync("Error populating conversion file list", ex);
                }
                catch
                {
                    /* Silently ignore */
                }
            });
        }
    }

    private async void BrowseOutputButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var outputFolder = await SelectFolderAsync("Select the output folder where RVZ files will be saved");
            if (string.IsNullOrEmpty(outputFolder)) return;

            OutputFolderTextBox.Text = outputFolder;
            LogMessage($"Output folder selected: {outputFolder}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method BrowseOutputButton_ClickAsync");
        }
    }

    private async void StartConversionButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Prevent starting multiple operations simultaneously
            if (_currentOperation != OperationType.None)
            {
                LogMessage(
                    $"Error: Cannot start conversion while a {_currentOperation.ToString().ToLowerInvariant()} operation is in progress.");
                await ShowErrorAsync(
                    $"Please wait for the current {_currentOperation.ToString().ToLowerInvariant()} operation to complete before starting a new one.");
                return;
            }

            var inputFolder = InputFolderTextBox.Text;
            var outputFolder = OutputFolderTextBox.Text;
            var deleteFiles = DeleteFilesCheckBox.IsChecked ?? false;
            var scrub = ScrubCheckBox.IsChecked ?? false;

            // Update compression settings from UI
            UpdateBlockSizeFromSelection();

            var inputError = ValidateFolder(inputFolder, "input folder", true);
            if (inputError != null)
            {
                LogMessage($"Error: {inputError}");
                await ShowErrorAsync(inputError);
                return;
            }

            var outputError = ValidateFolder(outputFolder, "output folder", false);
            if (outputError != null)
            {
                LogMessage($"Error: {outputError}");
                await ShowErrorAsync(outputError);
                return;
            }

            var selectedFiles = _conversionFiles.Where(static f => f.IsSelected).Select(static f => f.FullPath)
                .ToArray();
            if (selectedFiles.Length == 0)
            {
                LogMessage("Error: No files selected for conversion.");
                await ShowErrorAsync("Please select at least one file to convert.");
                return;
            }

            if (AreSameFolder(inputFolder, outputFolder))
            {
                const string msg = "The input and output folders must be different directories.";
                LogMessage($"Error: {msg}");
                await ShowErrorAsync(msg);
                return;
            }

            if (IsSubdirectory(inputFolder, outputFolder) || IsSubdirectory(outputFolder, inputFolder))
            {
                const string msg = "The input and output folders cannot be nested within each other.";
                LogMessage($"Error: {msg}");
                await ShowErrorAsync(msg);
                return;
            }

            try
            {
                Directory.CreateDirectory(outputFolder!);
            }
            catch (Exception ex)
            {
                LogMessage($"Error creating output directory {outputFolder}: {ex.Message}");
                await ShowErrorAsync($"Error creating output directory: {ex.Message}");
                await ReportBugAsync($"Error creating output directory: {outputFolder}", ex);
                return;
            }

            CancellationToken token;
            lock (_ctsLock)
            {
                // Always refresh the token source for a fresh start using proper disposal pattern
                using (_cts)
                {
                    _cts = new CancellationTokenSource();
                }

                token = _cts.Token;
            }

            // Clear the log before starting the conversion
            await Dispatcher.UIThread.InvokeAsync(ClearLogViewer);

            ResetOperationStats();
            _currentOperation = OperationType.Conversion;
            await SetControlsStateAsync(false);
            _operationTimer.Restart();
            _processingTimeUpdateTimer?.Start();

            LogMessage("Starting batch conversion process...");
            UpdateStatusBar("Starting conversion...");
            if (!string.IsNullOrEmpty(_dolphinToolPath))
            {
                LogMessage($"Using DolphinTool fallback: {_dolphinToolPath}");
            }
            LogMessage($"Input folder: {inputFolder}");
            LogMessage($"Output folder: {outputFolder}");
            LogMessage($"Delete original files: {deleteFiles}");
            LogMessage($"Scrub non-game Wii partitions: {scrub}");
            LogMessage(
                $"RVZ Compression: Method={_rvzCompressionMethod}, Level={_rvzCompressionLevel}, Block Size={_rvzBlockSize}");

            var fileProgress = new Progress<double>(UpdateFileProgress);

            // Wrap the whole job in a task that we can await on exit
            var wasCancelled = false;
            try
            {
                _runningTask =
                    Task.Run(
                        () => PerformBatchConversionAsync(_dolphinToolPath ?? string.Empty, selectedFiles, outputFolder!, deleteFiles,
                            token, scrub, fileProgress), token);

                await _runningTask.ConfigureAwait(false); // resume on thread pool, not UI thread
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
                LogMessage("Conversion cancelled by user.");
            }
            catch (Exception ex)
            {
                LogMessage($"Fatal conversion error: {ex.Message}");
                await ReportBugAsync("Unhandled exception in conversion", ex);
            }
            finally
            {
                _operationTimer.Stop();
                _processingTimeUpdateTimer?.Stop();
                UpdateProcessingTimeDisplay();
                UpdateWriteSpeedDisplay(0);
                UpdateStatusBar(wasCancelled ? "Conversion cancelled" : "Conversion completed");
                await SetControlsStateAsync(true);
                if (!wasCancelled)
                {
                    await LogOperationSummaryAsync("convert", "Conversion");
                }
                else
                {
                    LogMessage("--- Batch conversion cancelled. ---");
                }
            }
        }
        catch (Exception ex)
        {
            await ReportBugAsync("Error during StartConversionButton_ClickAsync", ex);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            lock (_ctsLock)
            {
                _cts.Cancel();
            }

            LogMessage("Cancellation requested. Waiting for current operation(s) to complete...");

            // Show appropriate overlay text based on current operation type
            var operationName = _currentOperation switch
            {
                OperationType.Conversion => "conversion",
                OperationType.Verification => "verification",
                OperationType.Extraction => "extraction",
                _ => "operation"
            };

            ExtractionOverlayText.Text =
                $"Cancellation requested.\nPlease wait for the current {operationName} to complete...";
            ExtractionOverlay.IsVisible = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method CancelButton_Click");
        }
    }

    private async Task SetControlsStateAsync(bool enabled)
    {
        // Use InvokeAsync to prevent UI freeze while ensuring UI updates complete.
        // This prevents deadlocks when called from background threads.
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                MainTabControl.IsEnabled = enabled;

                InputFolderTextBox.IsEnabled = enabled;
                OutputFolderTextBox.IsEnabled = enabled;
                BrowseInputButton.IsEnabled = enabled;
                BrowseOutputButton.IsEnabled = enabled;
                DeleteFilesCheckBox.IsEnabled = enabled;
                ScrubCheckBox.IsEnabled = enabled;
                StartConversionButton.IsEnabled = enabled;

                VerifyFolderTextBox.IsEnabled = enabled;
                BrowseVerifyFolderButton.IsEnabled = enabled;
                MoveFailedCheckBox.IsEnabled = enabled;
                MoveSuccessCheckBox.IsEnabled = enabled;
                StartVerifyButton.IsEnabled = enabled;

                ExtractInputFolderTextBox.IsEnabled = enabled;
                ExtractOutputFolderTextBox.IsEnabled = enabled;
                BrowseExtractInputButton.IsEnabled = enabled;
                BrowseExtractOutputButton.IsEnabled = enabled;
                DeleteExtractedFilesCheckBox.IsEnabled = enabled;
                StartExtractionButton.IsEnabled = enabled;

                CancelButton.IsVisible = !enabled;

                if (enabled) // If controls are enabled (operation finished or not started)
                {
                    ClearProgressDisplay(); // Set to idle state
                    _currentOperation = OperationType.None; // Reset operation type

                    // Update status bar to "Ready"
                    StatusBarText.Text = "Ready";

                    // Hide the "Please wait" overlay if it was shown during cancellation
                    ExtractionOverlay.IsVisible = false;
                }

                UpdateWriteSpeedDisplay(0);
            });
        }
        catch (TaskCanceledException)
        {
            // Expected during application shutdown
        }
        catch (InvalidOperationException)
        {
            // Dispatcher is shutting down
        }
    }

    private async Task<string?> SelectFolderAsync(string description)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = description,
                AllowMultiple = false
            });

            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        catch (Exception ex)
        {
            LogMessage($"Error opening folder picker: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Validates a folder path for basic correctness and accessibility.
    /// Returns an error message if validation fails, or null if validation passes.
    /// </summary>
    internal static string? ValidateFolder(string? folderPath, string label, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return $"Please select the {label}.";

        try
        {
            _ = Path.GetFullPath(folderPath);
        }
        catch (Exception)
        {
            return $"The {label} path is invalid: \"{folderPath}\"";
        }

        var driveRoot = Path.GetPathRoot(Path.GetFullPath(folderPath));
        if (!string.IsNullOrEmpty(driveRoot) && !Directory.Exists(driveRoot))
        {
            return
                $"The drive \"{driveRoot}\" containing the {label} is not available. Please reconnect the drive or choose a different {label}.";
        }

        switch (mustExist)
        {
            case true when !Directory.Exists(folderPath):
                return $"The {label} does not exist: \"{folderPath}\"";
            case true:
                try
                {
                    _ = Directory.EnumerateFiles(folderPath).Take(1).ToList();
                }
                catch (UnauthorizedAccessException)
                {
                    return $"Access denied to the {label}: \"{folderPath}\"";
                }
                catch (IOException ex)
                {
                    return $"Cannot access the {label}: {ex.Message}";
                }

                break;
        }

        return null;
    }

    /// <summary>
    /// Validates that input and output folders are not the same directory.
    /// </summary>
    internal static bool AreSameFolder(string? path1, string? path2)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path1) || string.IsNullOrWhiteSpace(path2)) return false;

            var full1 = Path.GetFullPath(path1).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full2 = Path.GetFullPath(path2).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(full1, full2, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Determines whether one folder path is nested inside another.
    /// </summary>
    /// <param name="parent">The potential parent folder.</param>
    /// <param name="child">The potential child folder.</param>
    /// <returns>true when <paramref name="child"/> is below <paramref name="parent"/>; otherwise, false.</returns>
    internal static bool IsSubdirectory(string? parent, string? child)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(child)) return false;

            var parentFull = Path.GetFullPath(parent)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var childFull = Path.GetFullPath(child)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return childFull.StartsWith(parentFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || childFull.StartsWith(parentFull + Path.AltDirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task PerformBatchConversionAsync(string dolphinToolPath, string[] files, string outputFolder,
        bool deleteFiles, CancellationToken token, bool scrub = false, IProgress<double>? fileProgress = null)
    {
        try
        {
            ResetOperationStats();

            lock (_statsLock)
            {
                _totalFilesToProcess = files.Length;
            }

            int totalFiles;
            lock (_statsLock)
            {
                totalFiles = _totalFilesToProcess;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FileProgressBar.IsIndeterminate = true;
                FileProgressBar.Value = 0;
                ProgressBar.Maximum = Math.Max(totalFiles, 1);
                ProgressBar.Value = 0;
            });

            lock (_speedLock)
            {
                _speedCalculationStartTime = DateTime.Now;
            }

            // Use the local token variable captured inside the lock for thread safety
            await _conversionService.PerformBatchConversionAsync(
                dolphinToolPath,
                files,
                outputFolder,
                deleteFiles,
                _rvzCompressionMethod,
                _rvzCompressionLevel,
                _rvzBlockSize,
                (processed, total, fileName) =>
                {
                    UpdateProgressDisplay(processed, total, fileName, "Converting");
                    UpdateOverallProgress();
                    UpdateStatsDisplay();
                    UpdateProcessingTimeDisplay();
                    // Track bytes for speed calculation - find file size from conversion files list
                    var fileItem = _conversionFiles.FirstOrDefault(f => f.FileName == fileName);
                    if (fileItem != null)
                    {
                        AddProcessedBytes(fileItem.FileSize);
                    }

                    CalculateAndUpdateWriteSpeed();
                },
                count =>
                {
                    lock (_statsLock)
                    {
                        _successCount += count;
                    }
                },
                count =>
                {
                    lock (_statsLock)
                    {
                        _failureCount += count;
                    }
                },
                token,
                scrub,
                fileProgress);
        }
        catch (OperationCanceledException)
        {
            LogMessage("Batch conversion operation was canceled.");
        }
        catch (Exception ex)
        {
            LogMessage($"Error during batch conversion: {ex.Message}");
            await ShowMessageBoxAsync($"Error during batch conversion: {ex.Message}", "Error", MessageBoxButton.Ok,
                MessageBoxImage.Error);
            await ReportBugAsync("Error during batch conversion operation", ex);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FileProgressBar.IsIndeterminate = false;
                FileProgressBar.Value = 0;
            });
        }
    }

    private async Task<MessageBoxResult> ShowMessageBoxAsync(string message, string title, MessageBoxButton buttons,
        MessageBoxImage icon)
    {
        try
        {
            // Only pass this window as the owner if it is still visible and not disposed.
            // This prevents invalid-owner errors if the window is in the process of closing.
            var owner = _disposed || !IsVisible ? null : this;
            return await MessageBox.ShowAsync(owner, message, title, buttons, icon);
        }
        catch (Exception ex)
        {
            LogMessage($"Failed to show message box: {ex.Message}");
            return MessageBoxResult.None;
        }
    }

    private Task<MessageBoxResult> ShowErrorAsync(string message)
    {
        return ShowMessageBoxAsync(message, "Error", MessageBoxButton.Ok, MessageBoxImage.Error);
    }

    /// <summary>
    /// Reports a bug through Serilog at Error level (when an exception is provided)
    /// or Warning level (message only). Warning+ events are automatically forwarded
    /// to the Bug Report API by <see cref="BugReportSink"/>.
    /// Corrupt-user-file failures should log at Information and call this.
    /// This method always returns a completed task for backward compatibility.
    /// </summary>
    private static Task ReportBugAsync(string message, Exception? exception = null)
    {
        try
        {
            if (exception != null)
            {
                Log.Error(exception, "{Message:l}", message);
            }
            else
            {
                Log.Warning("{Message:l}", message);
            }
        }
        catch
        {
            /* Silently fail reporting */
        }

        return Task.CompletedTask;
    }

    private void ExitMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method ExitMenuItem_Click");
        }
    }

    private void AboutMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var aboutWindow = new AboutWindow();
            _ = aboutWindow.ShowDialog(this);
        }
        catch (Exception ex)
        {
            LogMessage($"Error opening About window: {ex.Message}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await ReportBugAsync("Error opening About window", ex);
                }
                catch
                {
                    /* Silently ignore */
                }
            });
        }
    }

    private async void CheckForUpdatesMenuItem_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            await CheckForUpdatesAsync(true);
        }
        catch (Exception ex)
        {
            _ = ReportBugAsync("Error checking for updates", ex);
        }
    }

    private async Task CheckForUpdatesAsync(bool isManualCheck)
    {
        LogMessage("Checking for updates...");
        try
        {
            var (isUpdateAvailable, latestRelease) = await _updateService.CheckForUpdatesAsync();

            if (isUpdateAvailable && latestRelease != null)
            {
                LogMessage($"New version available: {latestRelease.Name}");
                var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
                var message = $"A new version ({latestRelease.Name}) is available!\n" +
                              $"You are currently using version {currentVersion}.\n\n" +
                              $"Release Notes:\n{latestRelease.Body}\n\n" +
                              "Would you like to go to the download page?";

                var result = await ShowMessageBoxAsync(message, "Update Available", MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    OpenUrl(latestRelease.HtmlUrl);
                }
            }
            else
            {
                LogMessage("You are using the latest version.");
                if (isManualCheck)
                {
                    await ShowMessageBoxAsync("You are already using the latest version.", "No Updates Found",
                        MessageBoxButton.Ok, MessageBoxImage.Information);
                }
            }
        }
        catch (HttpRequestException ex)
        {
            // HTTP status failures (rate limit, 404, 5xx) and network errors are external
            // service conditions, not application bugs, so they never create a bug report.
            var errorMessage = ex.StatusCode is null
                ? $"Failed to check for updates: network error ({ex.Message})"
                : $"Failed to check for updates: server returned {(int)ex.StatusCode} ({ex.StatusCode}).";
            LogMessage(errorMessage);
            if (isManualCheck)
            {
                await ShowMessageBoxAsync("Could not connect to update server. Please check your internet connection.",
                    "Update Check Failed", MessageBoxButton.Ok, MessageBoxImage.Warning);
            }
        }
        catch (TaskCanceledException ex)
        {
            var errorMessage = $"Update check timed out: {ex.Message}";
            LogMessage(errorMessage);
            if (isManualCheck)
            {
                await ShowMessageBoxAsync("Update check timed out. Please try again later.", "Update Check Failed",
                    MessageBoxButton.Ok, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            var errorMessage = $"Error checking for updates: {ex.Message}";
            LogMessage(errorMessage);
            if (isManualCheck)
            {
                await ShowMessageBoxAsync($"An error occurred while checking for updates:\n{ex.Message}",
                    "Update Check Failed", MessageBoxButton.Ok, MessageBoxImage.Error);
            }

            await ReportBugAsync("Failed to check for updates", ex);
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            var errorMessage = $"Error opening URL: {url}. Exception: {ex.Message}";
            LogMessage(errorMessage);
            _ = ReportBugAsync(errorMessage, ex);
            _ = ShowMessageBoxAsync($"Unable to open link: {ex.Message}", "Error", MessageBoxButton.Ok,
                MessageBoxImage.Error);
        }
    }

    private void ClearProgressDisplay()
    {
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                FileProgressBar.IsIndeterminate = false;
                FileProgressBar.Value = 0;
                FileProgressBar.Maximum = 1;
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 0;
                ProgressBar.Maximum = 1;
                StatusBarText.Text = "Ready."; // Set a default idle message
            });
        }
        catch (TaskCanceledException)
        {
            // Expected during application shutdown
        }
        catch (InvalidOperationException)
        {
            // Dispatcher is shutting down
        }
    }

    /// <summary>
    /// Releases all resources used by the <see cref="MainWindow"/>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            // Signal shutdown to ensure log processor exits quickly
            _isShuttingDown = true;

            // Complete the log channel to signal the log processor to exit
            _logChannel.Writer.TryComplete();

            // Wait for the log processor to finish with a reasonable timeout
            if (_logProcessorTask is { IsCompleted: false })
            {
                try
                {
                    // Use a longer timeout to ensure all pending logs are processed
                    // but don't block indefinitely
                    _logProcessorTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException ex) when
                    (ex.InnerException is OperationCanceledException or TaskCanceledException)
                {
                    // Expected during shutdown
                }
                catch (Exception ex)
                {
                    // Ignore any other exceptions during shutdown, but keep a trace
                    Log.Debug(ex, "Error waiting for the log processor to stop");
                }
            }

            lock (_ctsLock)
            {
                using (_cts)
                {
                    if (!_cts.IsCancellationRequested)
                    {
                        _cts.Cancel();
                    }
                }
            }

            _updateService.Dispose();
            _explorerSession?.Dispose();
            _operationTimer.Stop();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error during MainWindow disposal");
        }
        finally
        {
            GC.SuppressFinalize(this);
        }
    }

    private async void BrowseVerifyFolderButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var verifyFolder = await SelectFolderAsync("Select the folder containing RVZ files to verify");
            if (string.IsNullOrEmpty(verifyFolder)) return;

            VerifyFolderTextBox.Text = verifyFolder;
            LogMessage($"Verification folder selected: {verifyFolder}");

            PopulateVerificationFilesList(verifyFolder);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method BrowseVerifyFolderButton_ClickAsync");
        }
    }

    private void IncludeSubfoldersVerify_Changed(object? sender, RoutedEventArgs e)
    {
        try
        {
            var verifyFolder = VerifyFolderTextBox.Text;
            if (!string.IsNullOrEmpty(verifyFolder) && Directory.Exists(verifyFolder))
            {
                PopulateVerificationFilesList(verifyFolder);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method IncludeSubfoldersVerify_Changed");
        }
    }

    private void PopulateVerificationFilesList(string verifyFolder)
    {
        try
        {
            // Validate folder path before attempting to enumerate files
            if (string.IsNullOrWhiteSpace(verifyFolder))
            {
                LogMessage("Error: Verification folder path is empty.");
                return;
            }

            if (!Directory.Exists(verifyFolder))
            {
                LogMessage($"Error: Verification folder does not exist or is inaccessible: {verifyFolder}");
                return;
            }

            _verificationFiles.Clear();

            var searchOption = (IncludeSubfoldersVerifyCheckBox?.IsChecked ?? false)
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            var files = Directory.GetFiles(verifyFolder, "*.*", searchOption)
                .Where(file => FileService.IsRvzFile(file))
                .ToArray();

            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                _verificationFiles.Add(new FileItem
                {
                    FileName = Path.GetFileName(file),
                    FullPath = file,
                    FileSize = fileInfo.Length,
                    IsSelected = true
                });
            }

            VerificationFilesDataGrid.ItemsSource = _verificationFiles;
            var includeSubfolders = IncludeSubfoldersVerifyCheckBox?.IsChecked ?? false;
            LogMessage(
                $"Found {_verificationFiles.Count} RVZ files in verification folder {(includeSubfolders ? "(including subfolders)" : "(top level only)")}.");
        }
        catch (Exception ex)
        {
            LogMessage($"Error populating verification file list: {ex.Message}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await ReportBugAsync("Error populating verification file list", ex);
                }
                catch
                {
                    /* Silently ignore */
                }
            });
        }
    }

    /// <summary>
    /// Sets the <see cref="Models.FileItem.IsSelected"/> flag of every item in a file list.
    /// </summary>
    private static void SetAllSelected(IEnumerable<FileItem> files, bool isSelected)
    {
        foreach (var file in files)
        {
            file.IsSelected = isSelected;
        }
    }

    private void SelectAllConversion_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SetAllSelected(_conversionFiles, true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method SelectAllConversion_Click");
        }
    }

    private void DeselectAllConversion_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SetAllSelected(_conversionFiles, false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method DeselectAllConversion_Click");
        }
    }

    private void SelectAllVerification_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SetAllSelected(_verificationFiles, true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method SelectAllVerification_Click");
        }
    }

    private void DeselectAllVerification_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SetAllSelected(_verificationFiles, false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method DeselectAllVerification_Click");
        }
    }

    private async void StartVerifyButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Prevent starting multiple operations simultaneously
            if (_currentOperation != OperationType.None)
            {
                LogMessage(
                    $"Error: Cannot start verification while a {_currentOperation.ToString().ToLowerInvariant()} operation is in progress.");
                await ShowErrorAsync(
                    $"Please wait for the current {_currentOperation.ToString().ToLowerInvariant()} operation to complete before starting a new one.");
                return;
            }

            var verifyFolder = VerifyFolderTextBox.Text;

            _moveFailedFiles = MoveFailedCheckBox.IsChecked ?? false;
            _moveSuccessFiles = MoveSuccessCheckBox.IsChecked ?? false;

            var verifyError = ValidateFolder(verifyFolder, "verification folder", true);
            if (verifyError != null)
            {
                LogMessage($"Error: {verifyError}");
                await ShowErrorAsync(verifyError);
                return;
            }

            var selectedFiles = _verificationFiles.Where(static f => f.IsSelected).Select(static f => f.FullPath)
                .ToArray();
            if (selectedFiles.Length == 0)
            {
                LogMessage("Error: No files selected for verification.");
                await ShowErrorAsync("Please select at least one file to verify.");
                return;
            }

            CancellationToken token;
            lock (_ctsLock)
            {
                // Always refresh the token source for a fresh start using proper disposal pattern
                using (_cts)
                {
                    _cts = new CancellationTokenSource();
                }

                token = _cts.Token;
            }

            // Clear the log before starting the verification
            await Dispatcher.UIThread.InvokeAsync(ClearLogViewer);

            ResetOperationStats();
            _currentOperation = OperationType.Verification;
            await SetControlsStateAsync(false);
            _operationTimer.Restart();
            _processingTimeUpdateTimer?.Start();

            LogMessage("Starting batch verification process...");
            UpdateStatusBar("Starting verification...");
            if (!string.IsNullOrEmpty(_dolphinToolPath))
            {
                LogMessage($"Using DolphinTool fallback: {_dolphinToolPath}");
            }

            LogMessage($"Verification folder: {verifyFolder}");
            if (_moveFailedFiles) LogMessage("Failed files will be moved to '_Failed' subfolder.");
            if (_moveSuccessFiles) LogMessage("Successful files will be moved to '_Success' subfolder.");

            var wasCancelled = false;
            var fileProgress = new Progress<double>(UpdateFileProgress);
            _runningTask =
                Task.Run(
                    () => PerformBatchVerificationAsync(_dolphinToolPath ?? string.Empty, selectedFiles, _moveFailedFiles,
                        _moveSuccessFiles, token, fileProgress), token);

            try
            {
                await _runningTask.ConfigureAwait(false); // resume on thread pool, not UI thread
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
                LogMessage("Verification cancelled by user.");
            }
            catch (Exception ex)
            {
                LogMessage($"Fatal verification error: {ex.Message}");
                await ReportBugAsync("Unhandled exception in verification", ex);
            }
            finally
            {
                _operationTimer.Stop();
                _processingTimeUpdateTimer?.Stop();
                UpdateProcessingTimeDisplay();
                UpdateStatusBar(wasCancelled ? "Verification cancelled" : "Verification completed");
                await SetControlsStateAsync(true);
                if (!wasCancelled)
                {
                    await LogOperationSummaryAsync("verify", "Verification");
                }
                else
                {
                    LogMessage("--- Batch verification cancelled. ---");
                }

                // Refresh the verification file list to reflect any moved files
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!string.IsNullOrEmpty(VerifyFolderTextBox.Text) && Directory.Exists(VerifyFolderTextBox.Text))
                    {
                        PopulateVerificationFilesList(VerifyFolderTextBox.Text);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            await ReportBugAsync("Error during StartVerifyButton_ClickAsync", ex);
        }
    }

    private async Task PerformBatchVerificationAsync(string dolphinToolPath, string[] files, bool moveFailed,
        bool moveSuccess, CancellationToken token, IProgress<double>? fileProgress = null)
    {
        try
        {
            ResetOperationStats();

            lock (_statsLock)
            {
                _totalFilesToProcess = files.Length;
            }

            int totalFiles;
            lock (_statsLock)
            {
                totalFiles = _totalFilesToProcess;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FileProgressBar.IsIndeterminate = true;
                FileProgressBar.Value = 0;
                ProgressBar.Maximum = Math.Max(totalFiles, 1);
                ProgressBar.Value = 0;
            });

            lock (_speedLock)
            {
                _speedCalculationStartTime = DateTime.Now;
            }

            // Use the local token variable captured inside the lock for thread safety
            await _verificationService.PerformBatchVerificationAsync(
                dolphinToolPath,
                files,
                moveFailed,
                moveSuccess,
                (processed, total, fileName) =>
                {
                    UpdateProgressDisplay(processed, total, fileName, "Verifying");
                    UpdateOverallProgress();
                    UpdateStatsDisplay();
                    UpdateProcessingTimeDisplay();
                    // Track bytes for speed calculation - find file size from verification files list
                    var fileItem = _verificationFiles.FirstOrDefault(f => f.FileName == fileName);
                    if (fileItem != null)
                    {
                        AddProcessedBytes(fileItem.FileSize);
                    }

                    CalculateAndUpdateWriteSpeed();
                },
                count =>
                {
                    lock (_statsLock)
                    {
                        _successCount += count;
                    }
                },
                count =>
                {
                    lock (_statsLock)
                    {
                        _failureCount += count;
                    }
                },
                token,
                fileProgress);
        }
        catch (OperationCanceledException)
        {
            LogMessage("Batch verification operation was canceled.");
        }
        catch (Exception ex)
        {
            LogMessage($"Error during batch verification: {ex.Message}");
            await ShowMessageBoxAsync($"Error during batch verification: {ex.Message}", "Error", MessageBoxButton.Ok,
                MessageBoxImage.Error);
            await ReportBugAsync("Error during batch verification operation", ex);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FileProgressBar.IsIndeterminate = false;
                FileProgressBar.Value = 0;
            });
        }
    }

    private void ResetOperationStats()
    {
        lock (_statsLock)
        {
            _totalFilesToProcess = 0;
            _successCount = 0;
            _failureCount = 0;
        }

        _operationTimer.Reset();
        _processingTimeUpdateTimer?.Stop();

        // Reset speed calculation
        lock (_speedLock)
        {
            _totalBytesProcessed = 0;
            _speedCalculationStartTime = DateTime.Now;
        }

        UpdateStatsDisplay();
        UpdateProcessingTimeDisplay();
        UpdateWriteSpeedDisplay(0);
        ClearProgressDisplay();
    }

    /// <summary>
    /// Adds processed bytes to the speed calculation.
    /// </summary>
    /// <param name="bytes">Number of bytes processed.</param>
    private void AddProcessedBytes(long bytes)
    {
        lock (_speedLock)
        {
            _totalBytesProcessed += bytes;
        }
    }

    /// <summary>
    /// Calculates and updates the current write speed display.
    /// </summary>
    private void CalculateAndUpdateWriteSpeed()
    {
        double speedInMBps;
        lock (_speedLock)
        {
            var elapsed = DateTime.Now - _speedCalculationStartTime;
            if (elapsed.TotalSeconds > 0 && _totalBytesProcessed > 0)
            {
                speedInMBps = _totalBytesProcessed / (elapsed.TotalSeconds * 1024 * 1024);
            }
            else
            {
                speedInMBps = 0;
            }
        }

        UpdateWriteSpeedDisplay(speedInMBps);
    }

    private void UpdateStatsDisplay()
    {
        try
        {
            int totalFiles;
            int successCount;
            int failureCount;

            lock (_statsLock)
            {
                totalFiles = _totalFilesToProcess;
                successCount = _successCount;
                failureCount = _failureCount;
            }

            Dispatcher.UIThread.Post(() =>
            {
                TotalFilesValue.Text = totalFiles.ToString(CultureInfo.InvariantCulture);
                SuccessValue.Text = successCount.ToString(CultureInfo.InvariantCulture);
                FailedValue.Text = failureCount.ToString(CultureInfo.InvariantCulture);
            });
        }
        catch (TaskCanceledException)
        {
            // Expected during application shutdown
        }
        catch (InvalidOperationException)
        {
            // Dispatcher is shutting down
        }
    }

    private void UpdateProcessingTimeDisplay()
    {
        var elapsed = _operationTimer.Elapsed;
        try
        {
            Dispatcher.UIThread.Post(() =>
                ProcessingTimeValue.Text = $"{(int)elapsed.TotalHours:D2}:{elapsed:mm\\:ss}");
        }
        catch (TaskCanceledException)
        {
            // Expected during application shutdown
        }
        catch (InvalidOperationException)
        {
            // Dispatcher is shutting down
        }
    }

    private void UpdateWriteSpeedDisplay(double speedInMBps)
    {
        try
        {
            Dispatcher.UIThread.Post(() => WriteSpeedValue.Text = $"{speedInMBps:F1} MB/s");
        }
        catch (TaskCanceledException)
        {
            // Expected during application shutdown
        }
        catch (InvalidOperationException)
        {
            // Dispatcher is shutting down
        }
    }

    private void UpdateProgressDisplay(int current, int total, string currentFileName, string operationVerb)
    {
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                var percentage = total == 0 ? 0 : (double)current / total * 100;
                StatusBarText.Text =
                    $"{operationVerb} file {current} of {total}: {currentFileName} ({percentage:F1}%)";
            });
        }
        catch (TaskCanceledException)
        {
            // Expected during application shutdown
        }
        catch (InvalidOperationException)
        {
            // Dispatcher is shutting down
        }
    }

    /// <summary>
    /// Updates the per-file progress bar from a progress report (fraction in [0, 1]).
    /// </summary>
    private void UpdateFileProgress(double fraction)
    {
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                FileProgressBar.IsIndeterminate = false;
                FileProgressBar.Maximum = 100;
                FileProgressBar.Value = Math.Clamp(fraction * 100, 0, 100);
            });
        }
        catch (TaskCanceledException)
        {
            // Expected during application shutdown
        }
        catch (InvalidOperationException)
        {
            // Dispatcher is shutting down
        }
    }

    private async Task LogOperationSummaryAsync(string operationVerb, string operationNoun)
    {
        int totalFiles;
        int successCount;
        int failureCount;

        lock (_statsLock)
        {
            totalFiles = _totalFilesToProcess;
            successCount = _successCount;
            failureCount = _failureCount;
        }

        LogMessage("");
        LogMessage($"--- Batch {operationNoun} completed. ---");
        LogMessage($"Total files processed: {totalFiles}");
        LogMessage($"Successfully {GetPastTense(operationVerb)}: {successCount} files");
        if (failureCount > 0) LogMessage($"Failed to {operationVerb}: {failureCount} files");

        await ShowMessageBoxAsync($"Batch {operationNoun} completed.\n\n" +
                                  $"Total files processed: {totalFiles}\n" +
                                  $"Successfully {GetPastTense(operationVerb)}: {successCount} files\n" +
                                  $"Failed: {failureCount} files",
            $"{operationNoun} Complete", MessageBoxButton.Ok,
            failureCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    /// <summary>
    /// Converts a verb to its simple past tense for the operation summary messages
    /// (for example "convert" becomes "converted").
    /// </summary>
    /// <param name="verb">The verb to convert.</param>
    /// <returns>The past-tense form of the verb.</returns>
    internal static string GetPastTense(string verb)
    {
        verb = verb.ToLowerInvariant();

        if (verb.EndsWith('y') && verb.Length > 1)
        {
            // Check if the character before 'y' is a consonant (not a vowel)
            var beforeY = verb[^2];
            if (beforeY is not 'a' and not 'e' and not 'i' and not 'o' and not 'u')
            {
                return verb[..^1] + "ied";
            }
        }

        if (verb.EndsWith('e'))
        {
            return verb + "d";
        }

        // Double the final consonant for consonant-vowel-consonant verbs (scan -> scanned).
        if (verb.Length >= 3
            && IsConsonant(verb[^1])
            && IsVowel(verb[^2])
            && IsConsonant(verb[^3])
            && verb[^1] is not 'w' and not 'x' and not 'y')
        {
            return verb + verb[^1] + "ed";
        }

        return verb + "ed";
    }

    private static bool IsVowel(char value)
    {
        return value is 'a' or 'e' or 'i' or 'o' or 'u';
    }

    private static bool IsConsonant(char value)
    {
        return char.IsLetter(value) && !IsVowel(value);
    }

    /// <summary>
    /// Handles compression method selection change.
    /// Updates the compression level slider range based on the selected method.
    /// </summary>
    private void CompressionMethodComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (CompressionMethodComboBox?.SelectedItem is not ComboBoxItem selectedItem) return;
            if (CompressionLevelSlider == null) return;

            var method = selectedItem.Tag?.ToString() ?? "zstd";
            _rvzCompressionMethod = method;

            // Update compression level range based on selected method
            if (CompressionLevelRanges.TryGetValue(method, out var range))
            {
                CompressionLevelSlider.Minimum = range.Min;
                CompressionLevelSlider.Maximum = range.Max;

                // Adjust current value if it's outside the new range
                if (CompressionLevelSlider.Value < range.Min)
                {
                    CompressionLevelSlider.Value = range.Min;
                    _rvzCompressionLevel = range.Min;
                }
                else if (CompressionLevelSlider.Value > range.Max)
                {
                    CompressionLevelSlider.Value = range.Max;
                    _rvzCompressionLevel = range.Max;
                }
            }

            LogMessage(
                $"Compression method changed to: {method} (level range: {CompressionLevelSlider.Minimum}-{CompressionLevelSlider.Maximum})");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method CompressionMethodComboBox_SelectionChanged");
        }
    }

    /// <summary>
    /// Handles compression level slider value change.
    /// Updates the displayed value and stores the setting.
    /// </summary>
    private void CompressionLevelSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        try
        {
            if (CompressionLevelValue == null) return;

            var level = (int)e.NewValue;

            // Validate level is within allowed range for the selected compression method
            if (CompressionMethodComboBox?.SelectedItem is ComboBoxItem selectedItem)
            {
                var method = selectedItem.Tag?.ToString() ?? "zstd";

                if (CompressionLevelRanges.TryGetValue(method, out var range))
                {
                    // Ensure level is within valid range
                    if (level < range.Min)
                    {
                        level = range.Min;
                        CompressionLevelSlider.Value = level;
                    }
                    else if (level > range.Max)
                    {
                        level = range.Max;
                        CompressionLevelSlider.Value = level;
                    }
                }
            }

            _rvzCompressionLevel = level;
            CompressionLevelValue.Text = level.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method CompressionLevelSlider_ValueChanged");
        }
    }

    /// <summary>
    /// Gets the currently selected block size from the UI.
    /// Called when starting conversion to get the latest value.
    /// </summary>
    private void UpdateBlockSizeFromSelection()
    {
        if (BlockSizeComboBox?.SelectedItem is not ComboBoxItem selectedItem) return;

        if (selectedItem.Tag != null && int.TryParse(selectedItem.Tag.ToString(), out var blockSize))
        {
            // Add upper bound check (2MB) based on the defined values in the combo box
            if (blockSize is > 0 and <= 2097152)
            {
                _rvzBlockSize = blockSize;
            }
            else
            {
                LogMessage($"Warning: Invalid block size '{blockSize}' selected. Using default value.");
                _rvzBlockSize = 131072; // Default to 128KB
            }
        }
    }

    #region Extraction Tab Event Handlers

    private async void BrowseExtractInputButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var inputFolder = await SelectFolderAsync("Select the folder containing RVZ files to extract");
            if (string.IsNullOrEmpty(inputFolder)) return;

            ExtractInputFolderTextBox.Text = inputFolder;
            LogMessage($"Extraction input folder selected: {inputFolder}");

            PopulateExtractionFilesList(inputFolder);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method BrowseExtractInputButton_ClickAsync");
        }
    }

    private void PopulateExtractionFilesList(string inputFolder)
    {
        try
        {
            _extractionFiles.Clear();

            if (!Directory.Exists(inputFolder))
            {
                LogMessage($"Directory not found: {inputFolder}");
                ExtractionFilesDataGrid.ItemsSource = _extractionFiles;
                return;
            }

            var files = Directory.GetFiles(inputFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(file => FileService.IsRvzFile(file))
                .ToArray();

            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                _extractionFiles.Add(new FileItem
                {
                    FileName = Path.GetFileName(file),
                    FullPath = file,
                    FileSize = fileInfo.Length,
                    IsSelected = true
                });
            }

            ExtractionFilesDataGrid.ItemsSource = _extractionFiles;
            LogMessage($"Found {_extractionFiles.Count} RVZ files in extraction folder.");
        }
        catch (Exception ex)
        {
            LogMessage($"Error populating extraction file list: {ex.Message}");
        }
    }

    private async void BrowseExtractOutputButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var outputFolder = await SelectFolderAsync("Select the output folder where ISO files will be saved");
            if (string.IsNullOrEmpty(outputFolder)) return;

            ExtractOutputFolderTextBox.Text = outputFolder;
            LogMessage($"Extraction output folder selected: {outputFolder}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method BrowseExtractOutputButton_ClickAsync");
        }
    }

    private void SelectAllExtraction_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SetAllSelected(_extractionFiles, true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method SelectAllExtraction_Click");
        }
    }

    private void DeselectAllExtraction_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SetAllSelected(_extractionFiles, false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method DeselectAllExtraction_Click");
        }
    }

    private async void StartExtractionButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Prevent starting multiple operations simultaneously
            if (_currentOperation != OperationType.None)
            {
                LogMessage(
                    $"Error: Cannot start extraction while a {_currentOperation.ToString().ToLowerInvariant()} operation is in progress.");
                await ShowErrorAsync(
                    $"Please wait for the current {_currentOperation.ToString().ToLowerInvariant()} operation to complete before starting a new one.");
                return;
            }

            var inputFolder = ExtractInputFolderTextBox.Text;
            var outputFolder = ExtractOutputFolderTextBox.Text;
            var deleteFiles = DeleteExtractedFilesCheckBox.IsChecked ?? false;
            var outputFormat = (ExtractOutputFormatComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()?.ToLowerInvariant()
                               ?? "iso";

            var inputError = ValidateFolder(inputFolder, "input folder", true);
            if (inputError != null)
            {
                LogMessage($"Error: {inputError}");
                await ShowErrorAsync(inputError);
                return;
            }

            var outputError = ValidateFolder(outputFolder, "output folder", false);
            if (outputError != null)
            {
                LogMessage($"Error: {outputError}");
                await ShowErrorAsync(outputError);
                return;
            }

            var selectedFiles = _extractionFiles.Where(static f => f.IsSelected).Select(static f => f.FullPath)
                .ToArray();
            if (selectedFiles.Length == 0)
            {
                LogMessage("Error: No files selected for extraction.");
                await ShowErrorAsync("Please select at least one file to extract.");
                return;
            }

            if (AreSameFolder(inputFolder, outputFolder))
            {
                const string msg = "The input and output folders must be different directories.";
                LogMessage($"Error: {msg}");
                await ShowErrorAsync(msg);
                return;
            }

            if (IsSubdirectory(inputFolder, outputFolder) || IsSubdirectory(outputFolder, inputFolder))
            {
                const string msg = "The input and output folders cannot be nested within each other.";
                LogMessage($"Error: {msg}");
                await ShowErrorAsync(msg);
                return;
            }

            try
            {
                Directory.CreateDirectory(outputFolder!);
            }
            catch (Exception ex)
            {
                LogMessage($"Error creating output directory {outputFolder}: {ex.Message}");
                await ShowErrorAsync($"Error creating output directory: {ex.Message}");
                await ReportBugAsync($"Error creating output directory: {outputFolder}", ex);
                return;
            }

            CancellationToken token;
            lock (_ctsLock)
            {
                // Always refresh the token source for a fresh start using proper disposal pattern
                using (_cts)
                {
                    _cts = new CancellationTokenSource();
                }

                token = _cts.Token;
            }

            // Clear the log before starting the extraction
            await Dispatcher.UIThread.InvokeAsync(ClearLogViewer);

            ResetOperationStats();
            _currentOperation = OperationType.Extraction;
            await SetControlsStateAsync(false);
            _operationTimer.Restart();
            _processingTimeUpdateTimer?.Start();

            LogMessage("Starting batch extraction process...");
            UpdateStatusBar("Starting extraction...");
            if (!string.IsNullOrEmpty(_dolphinToolPath))
            {
                LogMessage($"Using DolphinTool fallback: {_dolphinToolPath}");
            }
            LogMessage($"Input folder: {inputFolder}");
            LogMessage($"Output folder: {outputFolder}");
            LogMessage($"Output format: {outputFormat.ToUpperInvariant()}");
            LogMessage($"Delete original files: {deleteFiles}");

            var wasCancelled = false;
            var fileProgress = new Progress<double>(UpdateFileProgress);
            try
            {
                _runningTask =
                    Task.Run(
                        () => PerformBatchExtractionAsync(_dolphinToolPath ?? string.Empty, selectedFiles, outputFolder!, deleteFiles,
                            outputFormat, token, fileProgress), token);

                await _runningTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
                LogMessage("Extraction cancelled by user.");
            }
            catch (Exception ex)
            {
                LogMessage($"Fatal extraction error: {ex.Message}");
                await ReportBugAsync("Unhandled exception in extraction", ex);
            }
            finally
            {
                _operationTimer.Stop();
                _processingTimeUpdateTimer?.Stop();
                UpdateProcessingTimeDisplay();
                UpdateWriteSpeedDisplay(0);
                UpdateStatusBar(wasCancelled ? "Extraction cancelled" : "Extraction completed");
                await SetControlsStateAsync(true);
                if (!wasCancelled)
                {
                    await LogOperationSummaryAsync("extract", "Extraction");
                }
                else
                {
                    LogMessage("--- Batch extraction cancelled. ---");
                }
            }
        }
        catch (Exception ex)
        {
            await ReportBugAsync("Error during StartExtractionButton_ClickAsync", ex);
        }
    }

    private async Task PerformBatchExtractionAsync(string dolphinToolPath, string[] files, string outputFolder,
        bool deleteFiles, string outputFormat, CancellationToken token, IProgress<double>? fileProgress = null)
    {
        try
        {
            ResetOperationStats();

            lock (_statsLock)
            {
                _totalFilesToProcess = files.Length;
            }

            int totalFiles;
            lock (_statsLock)
            {
                totalFiles = _totalFilesToProcess;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FileProgressBar.IsIndeterminate = true;
                FileProgressBar.Value = 0;
                ProgressBar.Maximum = Math.Max(totalFiles, 1);
                ProgressBar.Value = 0;
            });

            lock (_speedLock)
            {
                _speedCalculationStartTime = DateTime.Now;
            }

            // Use the local token variable captured inside the lock for thread safety
            await _extractionService.PerformBatchExtractionAsync(
                dolphinToolPath,
                files,
                outputFolder,
                deleteFiles,
                outputFormat,
                (processed, total, fileName) =>
                {
                    UpdateProgressDisplay(processed, total, fileName, "Extracting");
                    UpdateOverallProgress();
                    UpdateStatsDisplay();
                    UpdateProcessingTimeDisplay();
                    // Track bytes for speed calculation - find file size from extraction files list
                    var fileItem = _extractionFiles.FirstOrDefault(f => f.FileName == fileName);
                    if (fileItem != null)
                    {
                        AddProcessedBytes(fileItem.FileSize);
                    }

                    CalculateAndUpdateWriteSpeed();
                },
                count =>
                {
                    lock (_statsLock)
                    {
                        _successCount += count;
                    }
                },
                count =>
                {
                    lock (_statsLock)
                    {
                        _failureCount += count;
                    }
                },
                token,
                fileProgress);
        }
        catch (OperationCanceledException)
        {
            LogMessage("Batch extraction operation was canceled.");
        }
        catch (Exception ex)
        {
            LogMessage($"Error during batch extraction: {ex.Message}");
            await ShowMessageBoxAsync($"Error during batch extraction: {ex.Message}", "Error", MessageBoxButton.Ok,
                MessageBoxImage.Error);
            await ReportBugAsync("Error during batch extraction operation", ex);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FileProgressBar.IsIndeterminate = false;
                FileProgressBar.Value = 0;
            });
        }
    }

    #endregion

    #region Drag and Drop Event Handlers

    private void ConversionFilesDataGrid_DragOver(object? sender, DragEventArgs e)
    {
        try
        {
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method ConversionFilesDataGrid_DragOver");
        }
    }

    private void ConversionFilesDataGrid_Drop(object? sender, DragEventArgs e)
    {
        try
        {
            var files = GetDroppedFilePaths(e);
            if (files.Length > 0)
            {
                HandleDroppedFiles(files, "conversion");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method ConversionFilesDataGrid_Drop");
        }
    }

    private void VerificationFilesDataGrid_DragOver(object? sender, DragEventArgs e)
    {
        try
        {
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method VerificationFilesDataGrid_DragOver");
        }
    }

    private void VerificationFilesDataGrid_Drop(object? sender, DragEventArgs e)
    {
        try
        {
            var files = GetDroppedFilePaths(e);
            if (files.Length > 0)
            {
                HandleDroppedFiles(files, "verification");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method VerificationFilesDataGrid_Drop");
        }
    }

    private void ExtractionFilesDataGrid_DragOver(object? sender, DragEventArgs e)
    {
        try
        {
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method ExtractionFilesDataGrid_DragOver");
        }
    }

    private void ExtractionFilesDataGrid_Drop(object? sender, DragEventArgs e)
    {
        try
        {
            var files = GetDroppedFilePaths(e);
            if (files.Length > 0)
            {
                HandleDroppedFiles(files, "extraction");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method ExtractionFilesDataGrid_Drop");
        }
    }

    private static string[] GetDroppedFilePaths(DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return [];

        return e.DataTransfer.TryGetFiles()?
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrEmpty(path))
            .Cast<string>()
            .ToArray() ?? [];
    }

    private void HandleDroppedFiles(string[] files, string target)
    {
        if (files.Length == 0) return;

        try
        {
            var folderPath = files.FirstOrDefault(Directory.Exists);

            if (folderPath != null)
            {
                // If a folder was dropped, use it as the input folder
                switch (target)
                {
                    case "conversion":
                        InputFolderTextBox.Text = folderPath;
                        LogMessage($"Input folder selected via drag-and-drop: {folderPath}");
                        PopulateConversionFilesList(folderPath);
                        break;
                    case "verification":
                        VerifyFolderTextBox.Text = folderPath;
                        LogMessage($"Verification folder selected via drag-and-drop: {folderPath}");
                        PopulateVerificationFilesList(folderPath);
                        break;
                    case "extraction":
                        ExtractInputFolderTextBox.Text = folderPath;
                        LogMessage($"Extraction input folder selected via drag-and-drop: {folderPath}");
                        PopulateExtractionFilesList(folderPath);
                        break;
                }
            }
            else
            {
                // Individual files dropped - add them to the appropriate list
                var fileList = files.Where(File.Exists).ToList();
                if (fileList.Count > 0)
                {
                    LogMessage($"Dropped {fileList.Count} file(s) for {target}.");
                    AddIndividualFilesToList(fileList, target);
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Error handling dropped files: {ex.Message}");
            _ = ShowErrorAsync($"Error processing dropped files: {ex.Message}");
            _ = ReportBugAsync("Error handling dropped files", ex);
        }
    }

    /// <summary>
    /// Finds the deepest directory that contains every given file, or null when the files live
    /// in unrelated locations (for example on different drives or at the filesystem root).
    /// </summary>
    /// <param name="files">The full paths of the dropped files.</param>
    /// <returns>The common directory, or null when there is none.</returns>
    internal static string? GetCommonDirectory(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return null;
        }

        var commonDirectory = Path.GetDirectoryName(files[0]);
        for (var i = 1; i < files.Count && !string.IsNullOrEmpty(commonDirectory); i++)
        {
            var directory = Path.GetDirectoryName(files[i]) ?? string.Empty;
            while (!string.IsNullOrEmpty(commonDirectory) && !IsSameOrChildDirectory(directory, commonDirectory))
            {
                commonDirectory = Path.GetDirectoryName(commonDirectory);
            }
        }

        return string.IsNullOrEmpty(commonDirectory) ? null : commonDirectory;
    }

    /// <summary>
    /// Determines whether a directory is the same as, or nested inside, another directory.
    /// </summary>
    /// <param name="directory">The directory to test.</param>
    /// <param name="parent">The potential parent directory.</param>
    /// <returns>true when the directory is the parent or below it; otherwise, false.</returns>
    private static bool IsSameOrChildDirectory(string directory, string parent)
    {
        if (string.Equals(directory, parent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parentPrefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
        return directory.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds individual files to the appropriate file list based on the target.
    /// </summary>
    /// <param name="files">The list of file paths to add.</param>
    /// <param name="target">The target list ("conversion", "verification", or "extraction").</param>
    private void AddIndividualFilesToList(List<string> files, string target)
    {
        try
        {
            // Filter files by supported extensions based on target
            var filteredFiles = target switch
            {
                "conversion" => files.Where(file => FileService.IsSupportedInputFile(file)),
                "verification" => files.Where(file => FileService.IsRvzFile(file)),
                "extraction" => files.Where(file => FileService.IsSupportedExtractionInputFile(file)),
                _ => files
            };

            var fileArray = filteredFiles.ToArray();
            if (fileArray.Length == 0)
            {
                _ = ShowMessageBoxAsync("No supported files were found in the drop. Please check file extensions.", "Info",
                    MessageBoxButton.Ok, MessageBoxImage.Information);
                return;
            }

            // Get the appropriate list and determine the common parent directory
            var fileList = target switch
            {
                "conversion" => _conversionFiles,
                "verification" => _verificationFiles,
                "extraction" => _extractionFiles,
                _ => null
            };

            if (fileList == null) return;

            // Find common parent directory for the text box display
            var commonDirectory = GetCommonDirectory(fileArray);

            // Update the text box with the common directory or indicate multiple locations
            var textBox = target switch
            {
                "conversion" => InputFolderTextBox,
                "verification" => VerifyFolderTextBox,
                "extraction" => ExtractInputFolderTextBox,
                _ => null
            };

            textBox?.SetCurrentValue(TextBox.TextProperty, commonDirectory ?? "(Multiple locations)");

            // Add files to the list (skip duplicates)
            var addedCount = 0;
            var existingPaths = fileList.Select(static f => f.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var file in fileArray)
            {
                if (existingPaths.Contains(file))
                {
                    continue;
                }

                var fileInfo = new FileInfo(file);
                fileList.Add(new FileItem
                {
                    FileName = Path.GetFileName(file),
                    FullPath = file,
                    FileSize = fileInfo.Length,
                    IsSelected = true
                });
                addedCount++;
            }

            // Refresh the DataGrid binding
            switch (target)
            {
                case "conversion":
                    ConversionFilesDataGrid.ItemsSource = _conversionFiles;
                    break;
                case "verification":
                    VerificationFilesDataGrid.ItemsSource = _verificationFiles;
                    break;
                case "extraction":
                    ExtractionFilesDataGrid.ItemsSource = _extractionFiles;
                    break;
            }

            var skippedCount = fileArray.Length - addedCount;
            if (skippedCount > 0)
            {
                LogMessage(
                    $"Added {addedCount} file(s) to {target} list. {skippedCount} file(s) were already in the list.");
            }
            else
            {
                LogMessage($"Added {addedCount} file(s) to {target} list.");
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Error adding individual files: {ex.Message}");
            _ = ShowErrorAsync($"Error adding files: {ex.Message}");
            _ = ReportBugAsync("Error adding individual files to list", ex);
        }
    }

    #endregion

    #region Explorer Tab Event Handlers

    private async void BrowseExplorerImageButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var patterns = FileService.GetExplorerExtensions().Select(static ext => "*" + ext).ToList();
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select a disc image to explore",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Disc images") { Patterns = patterns },
                    FilePickerFileTypes.All
                ]
            });

            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (string.IsNullOrEmpty(path)) return;

            ExplorerImageTextBox.Text = path;
            ExplorerStatusText.Text = $"Selected {Path.GetFileName(path)}. Click Open to browse its contents.";
            LogMessage($"Explorer image selected: {path}");
        }
        catch (Exception ex)
        {
            LogMessage($"Error selecting explorer image: {ex.Message}");
            await ReportBugAsync("Error selecting explorer image", ex);
        }
    }

    private async void OpenExplorerImageButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = ExplorerImageTextBox.Text;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ExplorerStatusText.Text = "Select a disc image file first.";
                return;
            }

            await OpenExplorerImageAsync(path);
        }
        catch (Exception ex)
        {
            await ReportBugAsync("Error opening explorer image", ex);
        }
    }

    private async void RefreshExplorerButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_explorerSession is null) return;

            await OpenExplorerImageAsync(_explorerSession.ImagePath, _explorerSession.PartitionIndex);
        }
        catch (Exception ex)
        {
            await ReportBugAsync("Error refreshing explorer image", ex);
        }
    }

    private void CloseExplorerButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            CloseExplorerImage();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method CloseExplorerButton_Click");
        }
    }

    private async Task OpenExplorerImageAsync(string path, int partitionIndex = 0)
    {
        if (_isExplorerBusy) return;

        SetExplorerBusy(true);
        ExplorerStatusText.Text = $"Opening {Path.GetFileName(path)}...";

        try
        {
            var session = await Task.Run(() => _discExplorerService.TryOpen(path, partitionIndex));
            if (session is null)
            {
                ExplorerStatusText.Text = $"Could not open {Path.GetFileName(path)}. See the log for details.";
                return;
            }

            _explorerSession?.Dispose();
            _explorerSession = session;
            ExplorerImageTextBox.Text = path;
            PopulateExplorerTree();
            UpdateExplorerPartitionSelector();
            ExplorerVolumeText.Text = session.VolumeSummary;
            UpdateExplorerSelection(null);

            var partitionNote = session.PartitionCount > 0
                ? $", partition {DiscExplorerSession.PartitionName(session.Partitions[session.PartitionIndex].Type)}"
                : string.Empty;
            ExplorerStatusText.Text = $"Open: {session.Root.Children.Count} root entries{partitionNote}.";
            LogMessage($"Explorer opened: {path}");
        }
        catch (Exception ex)
        {
            LogMessage($"Explorer error: {ex.Message}");
            ExplorerStatusText.Text = $"Open failed: {ex.Message}";
            await ReportBugAsync("Error opening explorer image", ex);
        }
        finally
        {
            SetExplorerBusy(false);
        }
    }

    private async void ExplorerPartitionComboBox_SelectionChangedAsync(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            var session = _explorerSession;
            var index = ExplorerPartitionComboBox.SelectedIndex;
            if (session is null || _isExplorerBusy || index < 0 || index == session.PartitionIndex) return;

            SetExplorerBusy(true);
            var partitionName = DiscExplorerSession.PartitionName(session.Partitions[index].Type);
            ExplorerStatusText.Text = $"Switching to partition {partitionName}...";

            try
            {
                await Task.Run(() => session.SelectPartition(index));
                PopulateExplorerTree();
                ExplorerVolumeText.Text = session.VolumeSummary;
                UpdateExplorerSelection(null);
                ExplorerStatusText.Text =
                    $"Open: {session.Root.Children.Count} root entries in partition {partitionName}.";
            }
            finally
            {
                SetExplorerBusy(false);
            }
        }
        catch (Exception ex)
        {
            await ReportBugAsync("Error switching explorer partition", ex);
        }
    }

    private void ExplorerTreeView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            UpdateExplorerSelection(ExplorerTreeView.SelectedItem as ExplorerTreeNode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method ExplorerTreeView_SelectionChanged");
        }
    }

    private async void CopyOutExplorerButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var session = _explorerSession;
            if (session is null || _isExplorerBusy ||
                ExplorerTreeView.SelectedItem is not ExplorerTreeNode { IsDummy: false } node ||
                node.Data is null)
            {
                return;
            }

            string? destination;
            if (node.IsDirectory)
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = $"Copy '{node.Name}' to folder",
                    AllowMultiple = false
                });
                var folder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
                if (string.IsNullOrEmpty(folder)) return;

                destination = Path.Combine(folder, node.Name);
            }
            else
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = $"Copy '{node.Name}' out",
                    SuggestedFileName = node.Name,
                    DefaultExtension = Path.GetExtension(node.Name).TrimStart('.')
                });
                destination = file?.TryGetLocalPath();
                if (string.IsNullOrEmpty(destination)) return;
            }

            var data = node.Data;
            SetExplorerBusy(true);
            ExplorerStatusText.Text = $"Copying {node.FullPath}...";

            try
            {
                await Task.Run(() => session.CopyNodeTo(data, destination, CancellationToken.None));
                ExplorerStatusText.Text = $"Copied to {destination}.";
                LogMessage($"Explorer copy out: {node.FullPath} -> {destination}");
            }
            finally
            {
                SetExplorerBusy(false);
            }
        }
        catch (Exception ex)
        {
            ExplorerStatusText.Text = $"Copy failed: {ex.Message}";
            LogMessage($"Explorer copy failed: {ex.Message}");
            await ReportBugAsync("Error copying explorer entry", ex);
        }
    }

    private async void HashExplorerButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var session = _explorerSession;
            if (session is null || _isExplorerBusy ||
                ExplorerTreeView.SelectedItem is not ExplorerTreeNode { IsDummy: false, IsDirectory: false } node ||
                node.Data is null)
            {
                return;
            }

            var data = node.Data;
            SetExplorerBusy(true);
            ExplorerStatusText.Text = $"Hashing {node.FullPath}...";

            try
            {
                var hex = await Task.Run(() => session.ComputeSha256(data, CancellationToken.None));
                ExplorerHashText.Text = $"SHA-256({node.FullPath}) = {hex}";
                ExplorerStatusText.Text = "Hash complete.";
                LogMessage($"Explorer hash: {node.FullPath} = {hex}");
            }
            finally
            {
                SetExplorerBusy(false);
            }
        }
        catch (Exception ex)
        {
            ExplorerStatusText.Text = $"Hash failed: {ex.Message}";
            LogMessage($"Explorer hash failed: {ex.Message}");
            await ReportBugAsync("Error hashing explorer entry", ex);
        }
    }

    private void CloseExplorerImage()
    {
        _explorerSession?.Dispose();
        _explorerSession = null;
        _explorerRoots.Clear();
        ExplorerVolumeText.Text = "No image open.";
        ExplorerPartitionPanel.IsVisible = false;
        ExplorerPartitionComboBox.ItemsSource = null;
        ExplorerHashText.Text = string.Empty;
        UpdateExplorerSelection(null);
        SetExplorerBusy(false);
        ExplorerStatusText.Text = "Image closed.";
        LogMessage("Explorer image closed.");
    }

    private void PopulateExplorerTree()
    {
        _explorerRoots.Clear();
        if (_explorerSession is null) return;

        foreach (var child in _explorerSession.ListChildren(null))
        {
            _explorerRoots.Add(new ExplorerTreeNode(child));
        }
    }

    private void UpdateExplorerPartitionSelector()
    {
        if (_explorerSession is { PartitionCount: > 1 } session)
        {
            ExplorerPartitionComboBox.ItemsSource = session.Partitions
                .Select(partition => $"{DiscExplorerSession.PartitionName(partition.Type)} (0x{partition.Offset:X})")
                .ToList();
            ExplorerPartitionComboBox.SelectedIndex = session.PartitionIndex;
            ExplorerPartitionPanel.IsVisible = true;
        }
        else
        {
            ExplorerPartitionComboBox.ItemsSource = null;
            ExplorerPartitionPanel.IsVisible = false;
        }
    }

    private void UpdateExplorerSelection(ExplorerTreeNode? node)
    {
        ExplorerHashText.Text = string.Empty;

        if (node is null || node.IsDummy)
        {
            ExplorerDetailsTextBox.Text = "Select an entry in the tree.";
            CopyOutExplorerButton.IsEnabled = false;
            HashExplorerButton.IsEnabled = false;
            return;
        }

        var displayPath = string.IsNullOrEmpty(node.FullPath) ? "/" : "/" + node.FullPath;
        ExplorerDetailsTextBox.Text =
            $"Path: {displayPath}\n" +
            $"Type: {(node.IsDirectory ? "directory" : "file")}\n" +
            $"Size: {node.DisplaySize} ({node.Size:N0} bytes)\n" +
            $"Offset: 0x{node.Offset:X}";

        CopyOutExplorerButton.IsEnabled = !_isExplorerBusy;
        HashExplorerButton.IsEnabled = !_isExplorerBusy && !node.IsDirectory;
    }

    private void SetExplorerBusy(bool busy)
    {
        _isExplorerBusy = busy;

        var hasSession = _explorerSession is not null;
        var hasNode = ExplorerTreeView.SelectedItem is ExplorerTreeNode { IsDummy: false };
        var isFile = ExplorerTreeView.SelectedItem is ExplorerTreeNode { IsDummy: false, IsDirectory: false };

        BrowseExplorerImageButton.IsEnabled = !busy;
        OpenExplorerImageButton.IsEnabled = !busy;
        RefreshExplorerButton.IsEnabled = !busy && hasSession;
        CloseExplorerButton.IsEnabled = !busy && hasSession;
        ExplorerPartitionComboBox.IsEnabled = !busy;
        ExplorerTreeView.IsEnabled = !busy;
        CopyOutExplorerButton.IsEnabled = !busy && hasNode;
        HashExplorerButton.IsEnabled = !busy && isFile;
    }

    #endregion
}

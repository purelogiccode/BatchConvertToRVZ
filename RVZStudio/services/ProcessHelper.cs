using System.Runtime.InteropServices;

namespace RVZStudio.services;

/// <summary>
/// Helpers for locating, preparing and safely starting the external helper executables
/// (DolphinTool and 7za) used as fallbacks by the conversion, verification and extraction services.
/// </summary>
internal static class ProcessHelper
{
    private const uint SemFailcriticalerrors = 0x0001;
    private const uint SemNogpfaulterrorbox = 0x0002;
    private const uint SemNoopenfileerrorbox = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetProcessErrorMode(uint uMode);

    /// <summary>
    /// Returns true when the external executable exists on disk.
    /// </summary>
    internal static bool ExecutableExists(string? exePath)
    {
        return !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath);
    }

    /// <summary>
    /// Builds a user-friendly message for a missing helper executable. DolphinTool is an
    /// optional fallback (the native RVZSharp engine is primary), so this is an expected
    /// environment condition rather than an application bug; the message also hints at the
    /// common cause of running from a temporary extraction folder that gets deleted.
    /// </summary>
    internal static string GetMissingExecutableMessage(string exePath)
    {
        return $"The helper executable \"{Path.GetFileName(exePath)}\" was not found, so the fallback "
               + "engine is unavailable. The native RVZSharp engine handles supported files on its own; "
               + "place the helper next to the application to enable the fallback. If the application was "
               + "started from a temporary extraction folder (for example a WinRAR or other archiver temp "
               + "folder), the file may have been deleted while the application was running.";
    }

    /// <summary>
    /// Suppresses error dialogs (MessageBox) from child processes such as DolphinTool.
    /// This prevents blocking dialogs like "Ignore and continue?" from halting batch operations.
    /// The error mode is inherited by child processes.
    /// This is a Windows-only feature; it is a no-op on other operating systems.
    /// </summary>
    internal static void SuppressErrorDialogs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _ = SetProcessErrorMode(SemFailcriticalerrors | SemNogpfaulterrorbox | SemNoopenfileerrorbox);
        }
        catch (Exception ex)
        {
            // SetProcessErrorMode may not be available on all Windows versions.
            Serilog.Log.Debug(ex, "Failed to suppress child process error dialogs");
        }
    }

    /// <summary>
    /// Gets the path of the bundled 7za executable for the current architecture.
    /// Windows releases ship <c>7za.exe</c>; Linux/macOS builds use extension-less binaries.
    /// </summary>
    /// <returns>The full path of the 7za executable next to the application.</returns>
    internal static string Get7ZipExecutablePath()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        var suffix = architecture == Architecture.Arm64 ? "_arm64" : string.Empty;
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"7za{suffix}{extension}");
    }

    /// <summary>
    /// Ensures that a bundled helper executable can be executed on Unix-like systems
    /// by adding the execute permission bits when they are missing. This matters because
    /// ZIP archives do not preserve the executable bit on Linux and macOS.
    /// This is a no-op on Windows.
    /// </summary>
    internal static void EnsureExecutable(string? exePath)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return;
        }

        try
        {
            const UnixFileMode executeBits =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

            var mode = File.GetUnixFileMode(exePath);
            if ((mode & executeBits) != executeBits)
            {
                File.SetUnixFileMode(exePath, mode | executeBits);
            }
        }
        catch (Exception ex)
        {
            // Ignore permission errors; starting the process will report a clearer error.
            Serilog.Log.Debug(ex, "Failed to set execute permissions on {ExePath}", exePath);
        }
    }
}

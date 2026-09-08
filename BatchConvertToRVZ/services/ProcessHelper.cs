using System.IO;
using System.Runtime.InteropServices;

namespace BatchConvertToRVZ.services;

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
    /// Builds a user-friendly message for a missing external executable, hinting at the
    /// common cause of running the application from a temporary extraction folder that
    /// gets deleted while the application is running.
    /// </summary>
    internal static string GetMissingExecutableMessage(string exePath)
    {
        return $"Executable not found: \"{exePath}\". If the application was started from a temporary "
               + "extraction folder (for example a WinRAR or other archiver temp folder), the file may have "
               + "been deleted while the application was running. Extract the application to a permanent "
               + "folder and run it from there.";
    }

    /// <summary>
    /// Suppresses error dialogs (MessageBox) from child processes such as DolphinTool.
    /// This prevents blocking dialogs like "Ignore and continue?" from halting batch operations.
    /// The error mode is inherited by child processes.
    /// </summary>
    internal static void SuppressErrorDialogs()
    {
        try
        {
            _ = SetProcessErrorMode(SemFailcriticalerrors | SemNogpfaulterrorbox | SemNoopenfileerrorbox);
        }
        catch
        {
            // SetProcessErrorMode may not be available on all Windows versions
        }
    }
}

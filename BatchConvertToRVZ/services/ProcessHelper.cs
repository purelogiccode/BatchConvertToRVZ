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
    /// Suppresses error dialogs (MessageBox) from child processes such as DolphinTool.
    /// This prevents blocking dialogs like "Ignore and continue?" from halting batch operations.
    /// The error mode is inherited by child processes.
    /// </summary>
    internal static void SuppressErrorDialogs()
    {
        _ = SetProcessErrorMode(SemFailcriticalerrors | SemNogpfaulterrorbox | SemNoopenfileerrorbox);
    }
}

using System.Runtime.InteropServices;
using RVZStudio.services;
using Xunit;

namespace RVZStudio.Tests.Services;

public class ProcessHelperTests : IDisposable
{
    private readonly string _tempDir;

    public ProcessHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RVZStudio_ProcessHelperTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                /* ignore */
            }
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExecutableExistsReturnsFalseForMissingPath(string? path)
    {
        Assert.False(ProcessHelper.ExecutableExists(path));
    }

    [Fact]
    public void ExecutableExistsReturnsFalseForNonexistentFile()
    {
        Assert.False(ProcessHelper.ExecutableExists(Path.Combine(_tempDir, "missing.exe")));
    }

    [Fact]
    public void ExecutableExistsReturnsTrueForExistingFile()
    {
        var path = Path.Combine(_tempDir, "helper.exe");
        File.WriteAllText(path, "stub");

        Assert.True(ProcessHelper.ExecutableExists(path));
    }

    [Fact]
    public void GetMissingExecutableMessageContainsFileNameAndFallbackHint()
    {
        var message = ProcessHelper.GetMissingExecutableMessage(Path.Combine(_tempDir, "DolphinTool.exe"));

        Assert.Contains("DolphinTool.exe", message);
        Assert.Contains("fallback engine is unavailable", message);
        Assert.Contains("RVZSharp", message);
    }

    [Fact]
    public void Get7ZipExecutablePathReturnsRootedPathUnderBaseDirectory()
    {
        var path = ProcessHelper.Get7ZipExecutablePath();

        Assert.True(Path.IsPathRooted(path));
        Assert.StartsWith(AppDomain.CurrentDomain.BaseDirectory, path);
        Assert.Contains("7za", Path.GetFileName(path));
    }

    [Fact]
    public void Get7ZipExecutablePathUsesArchitectureSuffix()
    {
        var path = ProcessHelper.Get7ZipExecutablePath();
        var expectedSuffix = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "_arm64" : string.Empty;
        var expectedExtension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

        Assert.Equal($"7za{expectedSuffix}{expectedExtension}", Path.GetFileName(path));
    }

    [Fact]
    public void EnsureExecutableDoesNotThrowForMissingFile()
    {
        var exception = Record.Exception(() => ProcessHelper.EnsureExecutable(Path.Combine(_tempDir, "missing")));

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureExecutableDoesNotThrowForNull()
    {
        var exception = Record.Exception(() => ProcessHelper.EnsureExecutable(null));

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureExecutableAddsExecuteBitsOnUnix()
    {
        var path = Path.Combine(_tempDir, "helper");
        File.WriteAllText(path, "stub");

        if (OperatingSystem.IsWindows())
        {
            // Windows does not use Unix permission bits; the call must be a no-op.
            ProcessHelper.EnsureExecutable(path);
            Assert.True(File.Exists(path));
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        ProcessHelper.EnsureExecutable(path);

        var mode = File.GetUnixFileMode(path);
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        Assert.True(mode.HasFlag(UnixFileMode.GroupExecute));
        Assert.True(mode.HasFlag(UnixFileMode.OtherExecute));
    }

    [Fact]
    public void SuppressErrorDialogsDoesNotThrow()
    {
        var exception = Record.Exception(ProcessHelper.SuppressErrorDialogs);

        Assert.Null(exception);
    }
}

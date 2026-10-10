using System.Runtime.InteropServices;
using Xunit;

namespace RVZStudio.Tests;

public class MainWindowHelpersTests : IDisposable
{
    private readonly string _tempDir;

    public MainWindowHelpersTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RVZStudio_MainWindowTests_" + Path.GetRandomFileName());
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
    public void ValidateFolderReturnsErrorForMissingPath(string? path)
    {
        var error = MainWindow.ValidateFolder(path, "input folder", true);

        Assert.NotNull(error);
        Assert.Contains("input folder", error);
    }

    [Fact]
    public void ValidateFolderReturnsNullForExistingFolder()
    {
        var error = MainWindow.ValidateFolder(_tempDir, "input folder", true);

        Assert.Null(error);
    }

    [Fact]
    public void ValidateFolderReturnsNullForMissingOptionalFolder()
    {
        var missing = Path.Combine(_tempDir, "not-created");
        var error = MainWindow.ValidateFolder(missing, "output folder", false);

        Assert.Null(error);
    }

    [Fact]
    public void ValidateFolderReturnsErrorForMissingRequiredFolder()
    {
        var missing = Path.Combine(_tempDir, "not-created");
        var error = MainWindow.ValidateFolder(missing, "input folder", true);

        Assert.NotNull(error);
        Assert.Contains("does not exist", error);
    }

    [Fact]
    public void AreSameFolderReturnsTrueForIdenticalPaths()
    {
        Assert.True(MainWindow.AreSameFolder(_tempDir, _tempDir));
    }

    [Fact]
    public void AreSameFolderIgnoresTrailingSeparators()
    {
        Assert.True(MainWindow.AreSameFolder(_tempDir, _tempDir + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void AreSameFolderReturnsFalseForDifferentPaths()
    {
        var other = Path.Combine(_tempDir, "other");
        Assert.False(MainWindow.AreSameFolder(_tempDir, other));
    }

    [Theory]
    [InlineData(null, "x")]
    [InlineData("x", null)]
    [InlineData(null, null)]
    public void AreSameFolderReturnsFalseForNullPaths(string? first, string? second)
    {
        Assert.False(MainWindow.AreSameFolder(first, second));
    }

    [Fact]
    public void IsSubdirectoryReturnsTrueForNestedPath()
    {
        var child = Path.Combine(_tempDir, "child");

        Assert.True(MainWindow.IsSubdirectory(_tempDir, child));
    }

    [Fact]
    public void IsSubdirectoryReturnsFalseForParentPath()
    {
        var child = Path.Combine(_tempDir, "child");

        Assert.False(MainWindow.IsSubdirectory(child, _tempDir));
    }

    [Fact]
    public void IsSubdirectoryReturnsFalseForSiblingPath()
    {
        var first = Path.Combine(_tempDir, "first");
        var second = Path.Combine(_tempDir, "second");

        Assert.False(MainWindow.IsSubdirectory(first, second));
    }

    [Fact]
    public void IsSubdirectoryReturnsFalseForSamePath()
    {
        Assert.False(MainWindow.IsSubdirectory(_tempDir, _tempDir));
    }

    [Theory]
    [InlineData(null, "x")]
    [InlineData("x", null)]
    public void IsSubdirectoryReturnsFalseForNullPaths(string? parent, string? child)
    {
        Assert.False(MainWindow.IsSubdirectory(parent, child));
    }

    [Theory]
    [InlineData("convert", "converted")]
    [InlineData("verify", "verified")]
    [InlineData("extract", "extracted")]
    [InlineData("copy", "copied")]
    [InlineData("move", "moved")]
    [InlineData("scan", "scanned")]
    [InlineData("stop", "stopped")]
    [InlineData("play", "played")]
    [InlineData("fix", "fixed")]
    public void GetPastTenseReturnsExpectedWord(string verb, string expected)
    {
        Assert.Equal(expected, MainWindow.GetPastTense(verb));
    }

    [Fact]
    public void GetPastTenseLowercasesInput()
    {
        Assert.Equal("converted", MainWindow.GetPastTense("CONVERT"));
    }

    [Fact]
    public void GetDolphinToolExecutableNameMatchesArchitecture()
    {
        var expectedSuffix = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => string.Empty,
            Architecture.Arm64 => "_arm64",
            _ => null
        };

        if (expectedSuffix is null)
        {
            Assert.Throws<PlatformNotSupportedException>(MainWindow.GetDolphinToolExecutableName);
            return;
        }

        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        Assert.Equal($"DolphinTool{expectedSuffix}{extension}", MainWindow.GetDolphinToolExecutableName());
    }

    [Fact]
    public void GetCommonDirectoryReturnsNullForEmptyList()
    {
        Assert.Null(MainWindow.GetCommonDirectory([]));
    }

    [Fact]
    public void GetCommonDirectoryReturnsFolderForSingleFile()
    {
        var file = Path.Combine(_tempDir, "game.iso");

        Assert.Equal(_tempDir, MainWindow.GetCommonDirectory([file]));
    }

    [Fact]
    public void GetCommonDirectoryReturnsSharedFolder()
    {
        var first = Path.Combine(_tempDir, "a", "one.iso");
        var second = Path.Combine(_tempDir, "a", "two.iso");

        Assert.Equal(Path.Combine(_tempDir, "a"), MainWindow.GetCommonDirectory([first, second]));
    }

    [Fact]
    public void GetCommonDirectoryWalksUpToCommonParent()
    {
        var first = Path.Combine(_tempDir, "a", "one.iso");
        var second = Path.Combine(_tempDir, "b", "two.iso");

        Assert.Equal(_tempDir, MainWindow.GetCommonDirectory([first, second]));
    }

    [Fact]
    public void GetCommonDirectoryReturnsNullForRootlessPaths()
    {
        Assert.Null(MainWindow.GetCommonDirectory(["one.iso", "two.iso"]));
    }

    [Fact]
    public void GetCommonDirectoryIsCaseInsensitive()
    {
        var first = Path.Combine(_tempDir, "Game", "one.iso");
        var second = Path.Combine(_tempDir, "game", "two.iso");

        Assert.Equal(Path.Combine(_tempDir, "Game"), MainWindow.GetCommonDirectory([first, second]));
    }
}

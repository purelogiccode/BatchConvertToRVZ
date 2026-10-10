using RVZStudio.Models;
using Xunit;

namespace RVZStudio.Tests.Models;

public class SystemInfoTests
{
    [Fact]
    public void DefaultValuesAreEmpty()
    {
        var info = new SystemInfo();

        Assert.Equal(string.Empty, info.Date);
        Assert.Equal(string.Empty, info.ApplicationName);
        Assert.Equal(string.Empty, info.ApplicationVersion);
        Assert.Equal(string.Empty, info.OsVersion);
        Assert.Equal(string.Empty, info.Architecture);
        Assert.Equal(string.Empty, info.Bitness);
        Assert.Equal(string.Empty, info.WindowsVersion);
        Assert.Equal(0, info.ProcessorCount);
        Assert.Equal(string.Empty, info.BaseDirectory);
        Assert.Equal(string.Empty, info.TempPath);
    }

    [Fact]
    public void AllPropertiesRoundTrip()
    {
        var info = new SystemInfo
        {
            Date = "date",
            ApplicationName = "app",
            ApplicationVersion = "1.2.3",
            OsVersion = "os",
            Architecture = "X64",
            Bitness = "64-bit",
            WindowsVersion = "Windows 11",
            ProcessorCount = 12,
            BaseDirectory = "base",
            TempPath = "temp"
        };

        Assert.Equal("date", info.Date);
        Assert.Equal("app", info.ApplicationName);
        Assert.Equal("1.2.3", info.ApplicationVersion);
        Assert.Equal("os", info.OsVersion);
        Assert.Equal("X64", info.Architecture);
        Assert.Equal("64-bit", info.Bitness);
        Assert.Equal("Windows 11", info.WindowsVersion);
        Assert.Equal(12, info.ProcessorCount);
        Assert.Equal("base", info.BaseDirectory);
        Assert.Equal("temp", info.TempPath);
    }
}

public class OperationTypeTests
{
    [Fact]
    public void ContainsAllOperationValues()
    {
        var values = Enum.GetValues<OperationType>();

        Assert.Contains(OperationType.None, values);
        Assert.Contains(OperationType.Conversion, values);
        Assert.Contains(OperationType.Verification, values);
        Assert.Contains(OperationType.Extraction, values);
        Assert.Equal(4, values.Length);
    }

    [Fact]
    public void NoneIsTheDefaultValue()
    {
        Assert.Equal(OperationType.None, default);
    }
}

public class MessageBoxEnumTests
{
    [Fact]
    public void MessageBoxButtonContainsExpectedValues()
    {
        var values = Enum.GetValues<MessageBoxButton>();

        Assert.Contains(MessageBoxButton.Ok, values);
        Assert.Contains(MessageBoxButton.OkCancel, values);
        Assert.Contains(MessageBoxButton.YesNo, values);
    }

    [Fact]
    public void MessageBoxImageContainsExpectedValues()
    {
        var values = Enum.GetValues<MessageBoxImage>();

        Assert.Contains(MessageBoxImage.None, values);
        Assert.Contains(MessageBoxImage.Information, values);
        Assert.Contains(MessageBoxImage.Warning, values);
        Assert.Contains(MessageBoxImage.Error, values);
        Assert.Contains(MessageBoxImage.Question, values);
    }

    [Fact]
    public void MessageBoxResultContainsExpectedValues()
    {
        var values = Enum.GetValues<MessageBoxResult>();

        Assert.Contains(MessageBoxResult.None, values);
        Assert.Contains(MessageBoxResult.Ok, values);
        Assert.Contains(MessageBoxResult.Cancel, values);
        Assert.Contains(MessageBoxResult.Yes, values);
        Assert.Contains(MessageBoxResult.No, values);
    }
}

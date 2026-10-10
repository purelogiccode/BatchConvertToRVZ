using System.Text.Json;
using RVZStudio.Models;
using RVZStudio.services;
using Xunit;

namespace RVZStudio.Tests.Services;

public class BugReportServiceTests : IDisposable
{
    private const string ApiUrl = "https://bugreport.example.com/api/send-bug-report";
    private const string ApiKey = "test-api-key";
    private const string ApplicationName = "TestApp";

    private readonly BugReportService _service = new(ApiUrl, ApiKey, ApplicationName);

    public void Dispose()
    {
        _service.Dispose();
        GC.SuppressFinalize(this);
    }

    private static SystemInfo CreateSystemInfo()
    {
        return new SystemInfo
        {
            Date = "2026/01/02 03:04:05",
            ApplicationName = ApplicationName,
            ApplicationVersion = "9.9.9",
            OsVersion = "Microsoft Windows NT 10.0.22631.0",
            Architecture = "X64",
            Bitness = "64-bit",
            WindowsVersion = "Windows 11",
            ProcessorCount = 8,
            BaseDirectory = @"C:\Apps\RVZStudio\",
            TempPath = @"C:\Temp\"
        };
    }

    [Fact]
    public void GetWindowsVersionReturnsNonEmptyString()
    {
        var result = BugReportService.GetWindowsVersion();

        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void GetWindowsVersionReturnsStringContainingWindows()
    {
        var result = BugReportService.GetWindowsVersion();

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("Windows", result);
        }
        else
        {
            Assert.NotEmpty(result);
        }
    }

    [Fact]
    public void GetSystemInfoPopulatesEveryRequiredField()
    {
        var info = _service.GetSystemInfo();

        Assert.Equal(ApplicationName, info.ApplicationName);
        Assert.False(string.IsNullOrWhiteSpace(info.ApplicationVersion));
        Assert.False(string.IsNullOrWhiteSpace(info.Date));
        Assert.False(string.IsNullOrWhiteSpace(info.OsVersion));
        Assert.False(string.IsNullOrWhiteSpace(info.Architecture));
        Assert.Equal(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit", info.Bitness);
        Assert.False(string.IsNullOrWhiteSpace(info.WindowsVersion));
        Assert.Equal(Environment.ProcessorCount, info.ProcessorCount);
        Assert.Equal(AppDomain.CurrentDomain.BaseDirectory, info.BaseDirectory);
        Assert.Equal(Path.GetTempPath(), info.TempPath);
    }

    [Fact]
    public void BuildBugReportMessageContainsAllEnvironmentDetails()
    {
        var message = BugReportService.BuildBugReportMessage("Something broke", null, CreateSystemInfo());

        Assert.Contains("=== Environment Details ===", message);
        Assert.Contains("Date: 2026/01/02 03:04:05", message);
        Assert.Contains($"Application Name: {ApplicationName}", message);
        Assert.Contains("Application Version: 9.9.9", message);
        Assert.Contains("OS Version: Microsoft Windows NT 10.0.22631.0", message);
        Assert.Contains("Architecture: X64", message);
        Assert.Contains("Bitness: 64-bit", message);
        Assert.Contains("Windows, Linux or MacOsX Version: Windows 11", message);
        Assert.Contains("Processor Count: 8", message);
        Assert.Contains(@"Base Directory: C:\Apps\RVZStudio\", message);
        Assert.Contains(@"Temp Path: C:\Temp\", message);
    }

    [Fact]
    public void BuildBugReportMessageContainsErrorDetails()
    {
        var message = BugReportService.BuildBugReportMessage("Conversion failed", null, CreateSystemInfo());

        Assert.Contains("=== Error Details ===", message);
        Assert.Contains("Conversion failed", message);
    }

    [Fact]
    public void BuildBugReportMessageContainsExceptionDetails()
    {
        Exception exception;
        try
        {
            throw new InvalidOperationException("inner failure");
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        var message = BugReportService.BuildBugReportMessage("Crash", exception, CreateSystemInfo());

        Assert.Contains("=== Exception Details ===", message);
        Assert.Contains("Type: System.InvalidOperationException", message);
        Assert.Contains("Message: inner failure", message);
        Assert.Contains("Source:", message);
        Assert.Contains("StackTrace:", message);
    }

    [Fact]
    public void BuildBugReportMessageWithoutExceptionOmitsExceptionSection()
    {
        var message = BugReportService.BuildBugReportMessage("Just a message", null, CreateSystemInfo());

        Assert.DoesNotContain("=== Exception Details ===", message);
    }

    [Fact]
    public void BuildBugReportMessageIsTruncatedToApiLimit()
    {
        var message = BugReportService.BuildBugReportMessage(new string('x', 5000), null, CreateSystemInfo());

        Assert.True(message.Length <= 4000);
        Assert.EndsWith("...", message);
    }

    [Fact]
    public void GetEnvironmentShortIsTruncatedTo50Characters()
    {
        var info = CreateSystemInfo();
        info.WindowsVersion = new string('w', 80);

        var environment = BugReportService.GetEnvironmentShort(info);

        Assert.True(environment.Length <= 50);
    }

    [Theory]
    [InlineData("", 10, "")]
    [InlineData("short", 10, "short")]
    [InlineData("exactly10!", 10, "exactly10!")]
    [InlineData("this is too long", 10, "this is...")]
    public void TruncateStringBehavesAsExpected(string input, int maxLength, string expected)
    {
        Assert.Equal(expected, BugReportService.TruncateString(input, maxLength));
    }

    [Fact]
    public void BuildApiPayloadSerializesExpectedFields()
    {
        Exception exception;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        var payload = _service.BuildApiPayload("report message", exception, CreateSystemInfo());

        var json = JsonSerializer.Serialize(payload);
        using var document = JsonDocument.Parse(json);

        var root = document.RootElement;
        Assert.Contains("report message", root.GetProperty("message").GetString());
        Assert.Contains("Application Version: 9.9.9", root.GetProperty("message").GetString());
        Assert.Equal(ApplicationName, root.GetProperty("applicationName").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("version").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("environment").GetString()));
        Assert.Contains(nameof(BuildApiPayloadSerializesExpectedFields), root.GetProperty("stackTrace").GetString());
    }

    [Fact]
    public void BuildApiPayloadWithoutExceptionHasEmptyStackTrace()
    {
        var payload = _service.BuildApiPayload("report message", null, CreateSystemInfo());

        var json = JsonSerializer.Serialize(payload);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(string.Empty, document.RootElement.GetProperty("stackTrace").GetString());
    }

    [Fact]
    public void BuildApiPayloadStacktraceIsIncludedAndWithinApiLimit()
    {
        Exception exception;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        var payload = _service.BuildApiPayload("report", exception, CreateSystemInfo());

        var json = JsonSerializer.Serialize(payload);
        using var document = JsonDocument.Parse(json);

        var stackTrace = document.RootElement.GetProperty("stackTrace").GetString();
        Assert.NotNull(stackTrace);
        Assert.Contains(nameof(BuildApiPayloadStacktraceIsIncludedAndWithinApiLimit), stackTrace);
        Assert.True(stackTrace.Length <= 8000);
    }
}

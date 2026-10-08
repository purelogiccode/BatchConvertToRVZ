using RVZStudio.services;
using Xunit;

namespace RVZStudio.Tests.Services;

public class BugReportServiceTests
{
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
}

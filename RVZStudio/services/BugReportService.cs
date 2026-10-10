using System.Globalization;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using RVZStudio.Models;
using Serilog;

namespace RVZStudio.services;

/// <inheritdoc />
/// <summary>
/// Service responsible for sending bug reports to the BugReport API
/// </summary>
public class BugReportService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _applicationName;
    private readonly string _applicationVersion;

    /// <summary>
    /// Initializes a new instance of the <see cref="BugReportService"/> class.
    /// </summary>
    /// <param name="apiUrl">The URL of the BugReport API.</param>
    /// <param name="apiKey">The API key for authentication.</param>
    /// <param name="applicationName">The name of the application sending bug reports.</param>
    public BugReportService(string apiUrl, string apiKey, string applicationName)
    {
        _apiUrl = apiUrl;
        _apiKey = apiKey;
        _applicationName = applicationName;
        _applicationVersion = GetApplicationVersion();

        // Create a new HttpClient instance that shares the static handler
        // This allows per-instance headers while sharing the connection pool
        _httpClient = new HttpClient(SharedHttpHandler.Instance, false);

        // Set default headers once in the constructor for thread safety
        _httpClient.DefaultRequestHeaders.Add("X-API-KEY", _apiKey);
    }

    /// <summary>
    /// Sends a bug report to the API with system information
    /// </summary>
    /// <param name="message">The error message or bug report</param>
    /// <returns>A task representing the asynchronous operation</returns>
    public async Task<bool> SendBugReportAsync(string message)
    {
        try
        {
            var systemInfo = GetSystemInfo();
            var payload = BuildApiPayload(message, null, systemInfo);
            var content = JsonContent.Create(payload);
            var response = await _httpClient.PostAsync(_apiUrl, content);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            // Debug only: logging at Warning+ here would be forwarded back into this
            // service by the Bug Report sink and could loop.
            Log.Debug(ex, "Failed to send bug report: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Sends a bug report to the API with system information including exception details
    /// </summary>
    /// <param name="message">The error message or bug report</param>
    /// <param name="exception">The exception that occurred</param>
    /// <returns>A task representing the asynchronous operation</returns>
    public async Task<bool> SendBugReportAsync(string message, Exception exception)
    {
        try
        {
            var systemInfo = GetSystemInfo();
            var payload = BuildApiPayload(message, exception, systemInfo);
            var content = JsonContent.Create(payload);
            var response = await _httpClient.PostAsync(_apiUrl, content);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            // Debug only: logging at Warning+ here would be forwarded back into this
            // service by the Bug Report sink and could loop.
            Log.Debug(ex, "Failed to send bug report: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Gets the current application version from the assembly
    /// </summary>
    private static string GetApplicationVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version?.ToString() ?? "Unknown";
    }

    /// <summary>
    /// Builds the API-compatible payload matching the BugReportRequest schema expected by the API.
    /// Packs all environment and exception details into the message field since the API only
    /// recognizes: message, applicationName, version, userInfo, environment, stackTrace.
    /// </summary>
    internal object BuildApiPayload(string message, Exception? exception, SystemInfo systemInfo)
    {
        return new
        {
            message = BuildBugReportMessage(message, exception, systemInfo),
            applicationName = _applicationName,
            version = _applicationVersion,
            environment = GetEnvironmentShort(systemInfo),
            stackTrace = TruncateString(exception?.StackTrace ?? "", 8000)
        };
    }

    /// <summary>
    /// Builds the complete bug report message with environment details, error details,
    /// and exception details in separate sections.
    /// </summary>
    internal static string BuildBugReportMessage(string message, Exception? exception, SystemInfo systemInfo)
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== Environment Details ===");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Date: {systemInfo.Date}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Application Name: {systemInfo.ApplicationName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Application Version: {systemInfo.ApplicationVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS Version: {systemInfo.OsVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Architecture: {systemInfo.Architecture}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Bitness: {systemInfo.Bitness}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Windows, Linux or MacOsX Version: {systemInfo.WindowsVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Processor Count: {systemInfo.ProcessorCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Base Directory: {systemInfo.BaseDirectory}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Temp Path: {systemInfo.TempPath}");

        sb.AppendLine();
        sb.AppendLine("=== Error Details ===");
        sb.AppendLine(message);

        if (exception != null)
        {
            sb.AppendLine();
            sb.AppendLine("=== Exception Details ===");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Type: {exception.GetType().FullName}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Message: {exception.Message}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Source: {exception.Source ?? "Unknown"}");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"StackTrace: {TruncateString(exception.StackTrace ?? "Unknown", 1500)}");
        }

        return TruncateString(sb.ToString(), 4000);
    }

    /// <summary>
    /// Returns a short environment summary for the API's environment field (max 50 chars).
    /// </summary>
    internal static string GetEnvironmentShort(SystemInfo systemInfo)
    {
        return TruncateString($"{systemInfo.WindowsVersion} {systemInfo.Bitness}", 50);
    }

    /// <summary>
    /// Truncates a string to the specified maximum length, appending "..." if truncated.
    /// </summary>
    internal static string TruncateString(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;

        return value[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// Gets system information for the bug report
    /// </summary>
    internal SystemInfo GetSystemInfo()
    {
        var systemInfo = new SystemInfo
        {
            Date = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture),
            ApplicationName = _applicationName,
            ApplicationVersion = _applicationVersion,
            OsVersion = Environment.OSVersion.ToString(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant(),
            Bitness = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit",
            WindowsVersion = GetWindowsVersion(),
            ProcessorCount = Environment.ProcessorCount,
            BaseDirectory = AppDomain.CurrentDomain.BaseDirectory,
            TempPath = Path.GetTempPath()
        };

        return systemInfo;
    }

    /// <summary>
    /// Gets a friendly operating system version name
    /// </summary>
    internal static string GetWindowsVersion()
    {
        var osVersion = Environment.OSVersion;
        if (osVersion.Platform == PlatformID.Win32NT)
        {
            var version = osVersion.Version;
            switch (version.Major)
            {
                case 10 when version.Build >= 22000:
                    return "Windows 11";
                case 10:
                    return "Windows 10";
                case 6 when version.Minor == 3:
                    return "Windows 8.1";
                case 6 when version.Minor == 2:
                    return "Windows 8";
                case 6 when version.Minor == 1:
                    return "Windows 7";
            }

            return "Unknown Windows Version";
        }

        if (OperatingSystem.IsLinux())
        {
            return "Linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        return "Unknown OS";
    }

    /// <summary>
    /// Releases the HTTP client used by this service.
    /// </summary>
    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}

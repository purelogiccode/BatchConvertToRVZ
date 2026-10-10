using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using Serilog;

namespace RVZStudio.services;

/// <summary>
/// Service responsible for sending application usage statistics to the Stats API.
/// </summary>
public class StatsService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _applicationId;
    private readonly string _applicationVersion;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatsService"/> class.
    /// </summary>
    /// <param name="apiUrl">The URL of the Stats API.</param>
    /// <param name="apiKey">The API key for authentication.</param>
    /// <param name="applicationId">The unique identifier for the application.</param>
    public StatsService(string apiUrl, string apiKey, string applicationId)
        : this(apiUrl, apiKey, applicationId, SharedHttpHandler.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance with a custom <see cref="HttpMessageHandler"/> for testing.
    /// </summary>
    internal StatsService(string apiUrl, string apiKey, string applicationId, HttpMessageHandler handler)
    {
        _apiUrl = apiUrl;
        _apiKey = apiKey;
        _applicationId = applicationId;
        _applicationVersion = GetApplicationVersion();

        _httpClient = new HttpClient(handler, false);

        // Use Bearer token for authentication as required by the Stats API
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    /// <summary>
    /// Sends usage statistics to the API.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SendUsageStatsAsync()
    {
        try
        {
            var payload = new
            {
                applicationId = _applicationId,
                version = _applicationVersion
            };

            var response = await _httpClient.PostAsJsonAsync(_apiUrl, payload);

            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                Log.Debug("Failed to send usage stats: {Message}", DescribeFailure(response.StatusCode, content));
            }
        }
        catch (HttpRequestException ex)
        {
            Log.Debug("Failed to send usage stats (network): {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to send usage stats: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Builds a human-readable description for a non-success statistics response.
    /// </summary>
    /// <param name="statusCode">The HTTP status code returned by the API.</param>
    /// <param name="content">The response body.</param>
    /// <returns>A detailed description of the failure.</returns>
    private string DescribeFailure(System.Net.HttpStatusCode statusCode, string content)
    {
        return (int)statusCode switch
        {
            429 => $"Stats API Rate Limit: This IP has already reported stats for '{_applicationId}' within the rate limit period (usually 1 hour).",
            400 => $"Stats API Bad Request (400): The request was malformed or missing required fields. Response: {content}",
            401 => "Stats API Unauthorized (401): Invalid or missing API key.",
            403 => "Stats API Forbidden (403): API key does not have permission to access this resource.",
            404 => $"Stats API Not Found (404): The requested endpoint '{_apiUrl}' does not exist.",
            500 => $"Stats API Server Error (500): The server encountered an internal error. Response: {content}",
            502 => "Stats API Bad Gateway (502): The server received an invalid response from an upstream server.",
            503 => $"Stats API Service Unavailable (503): The server is temporarily unavailable. Response: {content}",
            _ => $"Stats API failed with status {statusCode}: {content}"
        };
    }

    private static string GetApplicationVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version?.ToString() ?? "Unknown";
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

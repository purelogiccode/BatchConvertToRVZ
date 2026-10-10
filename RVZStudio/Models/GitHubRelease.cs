using System.Text.Json.Serialization;

namespace RVZStudio.Models;

/// <summary>
/// Represents the structure of a GitHub release JSON response.
/// </summary>
public sealed class GitHubRelease
{
    /// <summary>Gets or sets the release tag (for example "v2.4.1").</summary>
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;

    /// <summary>Gets or sets the web page URL of the release.</summary>
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name of the release.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the release notes (Markdown body).</summary>
    [JsonPropertyName("body")] public string Body { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this is a pre-release.</summary>
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }

    /// <summary>Gets or sets a value indicating whether this is a draft release.</summary>
    [JsonPropertyName("draft")] public bool Draft { get; set; }
}

using System.IO;
using System.Net.Http;
using System.Text.Json;
using MemeMomo.Models;

namespace MemeMomo.Services;

internal sealed class GitHubReleaseService(HttpClient? client = null)
{
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _client = client ?? SharedClient;

    public async Task<GitHubRelease> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get, "https://api.github.com/repos/Turing158/MemeMomo/releases?per_page=1");
        request.Headers.UserAgent.ParseAdd("MemeMomo");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        JsonElement releases = document.RootElement;
        if (releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() == 0)
        {
            throw new JsonException("The releases response must contain at least one release.");
        }

        JsonElement release = releases[0];
        if (release.ValueKind != JsonValueKind.Object ||
            !release.TryGetProperty("tag_name", out JsonElement tag) || tag.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(tag.GetString()) ||
            !release.TryGetProperty("html_url", out JsonElement url) || url.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? releaseUri) ||
            (releaseUri.Scheme != Uri.UriSchemeHttps && releaseUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new JsonException("The release must contain a tag_name and an HTTP(S) html_url.");
        }

        return new GitHubRelease(tag.GetString()!, releaseUri);
    }
}

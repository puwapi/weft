using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Core.Protocol;

namespace Weft.Core.Release;

/// <summary>The published release, as the feed describes it.</summary>
public sealed record PublishedRelease(
    [property: JsonPropertyName("tag_name")] string Tag,
    [property: JsonPropertyName("html_url")] string? Url);

[JsonSerializable(typeof(PublishedRelease))]
public sealed partial class ReleaseJson : JsonSerializerContext;

/// <summary>The download did not match the digest published beside it.</summary>
public sealed class ChecksumMismatchException(string expected, string actual)
    : Exception($"the download does not match its published checksum (expected {expected}, got {actual})");

/// <summary>Fetches releases and their assets.</summary>
public sealed class ReleaseFeed : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _repo;

    public ReleaseFeed(string repo = "puwapi/weft", HttpMessageHandler? handler = null)
    {
        _repo = repo;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.Add("User-Agent", $"weft/{WeftVersion.Build}");
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    public void Dispose() => _http.Dispose();

    public async Task<PublishedRelease?> LatestAsync(CancellationToken ct = default)
    {
        using var r = await _http.GetAsync($"https://api.github.com/repos/{_repo}/releases/latest", ct)
            .ConfigureAwait(false);

        if (!r.IsSuccessStatusCode) return null;

        try { return await r.Content.ReadFromJsonAsync(ReleaseJson.Default.PublishedRelease, ct).ConfigureAwait(false); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Downloads an asset and checks it against the release's SHA256SUMS.
    /// </summary>
    /// <remarks>
    /// The verification is not optional and there is no flag to skip it. This
    /// replaces the binary the user runs; a download nobody checked is a way to
    /// hand somebody else's code the same trust.
    /// </remarks>
    public async Task<byte[]> DownloadVerifiedAsync(string tag, string asset, CancellationToken ct = default)
    {
        var baseUrl = $"https://github.com/{_repo}/releases/download/{tag}";

        var sums = await _http.GetStringAsync($"{baseUrl}/SHA256SUMS", ct).ConfigureAwait(false);
        var expected = Checksums.For(sums, asset)
            ?? throw new InvalidOperationException(
                $"release {tag} publishes no checksum for '{asset}', so the download cannot be verified");

        var bytes = await _http.GetByteArrayAsync($"{baseUrl}/{asset}", ct).ConfigureAwait(false);
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));

        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(actual),
                System.Text.Encoding.ASCII.GetBytes(expected)))
            throw new ChecksumMismatchException(expected, actual);

        return bytes;
    }
}

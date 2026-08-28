using System.Net;
using System.Security.Cryptography;
using System.Text;
using Weft.Core.Release;

namespace Weft.Core.Tests.Release;

/// <summary>
/// The refusal paths, which the real release cannot exercise.
/// </summary>
/// <remarks>
/// A published artefact always matches its own checksum, so downloading one only
/// ever proves the happy path. What matters here is what happens when it does
/// not: this replaces the binary the user runs, and a download nobody checked is
/// a way to hand somebody else's code the same trust.
/// </remarks>
public class ReleaseFeedTests
{
    private sealed class Canned(Func<string, (HttpStatusCode, byte[])> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = respond(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    private static string Digest(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static ReleaseFeed Feed(byte[] asset, string sums)
        => new("owner/repo", new Canned(url =>
            url.EndsWith("SHA256SUMS", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, Utf8(sums))
                : (HttpStatusCode.OK, asset)));

    [Fact]
    public async Task A_download_that_matches_its_checksum_is_returned()
    {
        var payload = Utf8("a binary, pretend");
        using var feed = Feed(payload, $"{Digest(payload)}  weft-linux-x64");

        Assert.Equal(payload, await feed.DownloadVerifiedAsync("v1.0.0", "weft-linux-x64"));
    }

    [Fact]
    public async Task A_download_that_does_not_match_is_refused()
    {
        // The case the whole verification exists for: the bytes arriving are not
        // the bytes that were published.
        var served = Utf8("something else entirely");
        var expected = Digest(Utf8("what was published"));

        using var feed = Feed(served, $"{expected}  weft-linux-x64");

        var e = await Assert.ThrowsAsync<ChecksumMismatchException>(
            () => feed.DownloadVerifiedAsync("v1.0.0", "weft-linux-x64"));

        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_single_flipped_bit_is_caught()
    {
        var published = Utf8(new string('x', 4096));
        var expected = Digest(published);

        var corrupted = (byte[])published.Clone();
        corrupted[2048] ^= 0x01;

        using var feed = Feed(corrupted, $"{expected}  weft-linux-x64");

        await Assert.ThrowsAsync<ChecksumMismatchException>(
            () => feed.DownloadVerifiedAsync("v1.0.0", "weft-linux-x64"));
    }

    [Fact]
    public async Task An_asset_with_no_published_checksum_is_refused()
    {
        // Not "verified anyway" and not "skipped with a warning". An artefact that
        // no checksum covers is one nothing vouches for.
        using var feed = Feed(Utf8("payload"), "abc123  some-other-asset");

        var e = await Assert.ThrowsAsync<InvalidOperationException>(
            () => feed.DownloadVerifiedAsync("v1.0.0", "weft-linux-x64"));

        Assert.Contains("cannot be verified", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_checksums_file_is_refused_rather_than_trusted()
    {
        using var feed = Feed(Utf8("payload"), "");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => feed.DownloadVerifiedAsync("v1.0.0", "weft-linux-x64"));
    }

    [Fact]
    public async Task A_feed_that_answers_with_nothing_useful_yields_no_release()
    {
        using var feed = new ReleaseFeed("owner/repo",
            new Canned(_ => (HttpStatusCode.OK, Utf8("<html>not json</html>"))));

        Assert.Null(await feed.LatestAsync());
    }

    [Fact]
    public async Task A_repository_with_no_releases_yields_no_release()
    {
        using var feed = new ReleaseFeed("owner/repo",
            new Canned(_ => (HttpStatusCode.NotFound, Utf8("{}"))));

        Assert.Null(await feed.LatestAsync());
    }

    [Fact]
    public async Task The_tag_is_read_from_the_feed()
    {
        using var feed = new ReleaseFeed("owner/repo",
            new Canned(_ => (HttpStatusCode.OK, Utf8("""{"tag_name":"v9.9.9","html_url":"https://x/y"}"""))));

        var latest = await feed.LatestAsync();
        Assert.Equal("v9.9.9", latest!.Tag);
    }
}

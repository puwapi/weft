using Weft.Core.Remote;

namespace Weft.Core.Tests.Remote;

/// <summary>
/// The credentials that travel outside the encrypted store are the join secret
/// and the bearer token. These check the one place that decides whether they are
/// allowed onto an unprotected wire.
/// </summary>
public sealed class RemoteUrlTests
{
    [Fact]
    public void Https_is_accepted_and_the_trailing_slash_goes()
    {
        var v = RemoteUrl.Check("https://weft.example/");

        Assert.True(v.Ok);
        Assert.Equal("https://weft.example", v.Url);
        Assert.Null(v.Warning);
    }

    [Fact]
    public void A_path_survives_normalisation()
    {
        // A reverse proxy that mounts weft under a prefix is a normal deployment,
        // and dropping the prefix would send every request to the wrong place.
        var v = RemoteUrl.Check("https://example.com/weft/");

        Assert.True(v.Ok);
        Assert.Equal("https://example.com/weft", v.Url);
    }

    [Fact]
    public void A_bare_host_is_assumed_to_be_https()
    {
        // Assuming the safe scheme can only ever improve on what was typed.
        // Assuming the other one silently would be the whole bug this prevents.
        var v = RemoteUrl.Check("weft.example");

        Assert.True(v.Ok);
        Assert.Equal("https://weft.example", v.Url);
    }

    [Fact]
    public void Plain_http_to_a_real_host_is_refused()
    {
        var v = RemoteUrl.Check("http://weft.example");

        Assert.False(v.Ok);
        Assert.Contains("plain HTTP", v.Refusal);
    }

    [Fact]
    public void The_refusal_says_what_would_leak_and_what_would_not()
    {
        // A refusal that overstates the danger gets bypassed by reflex. Content
        // really is safe here; the credentials really are not, and the message
        // has to carry both halves or it teaches the wrong lesson.
        var refusal = RemoteUrl.Check("http://weft.example").Refusal!;

        Assert.Contains("encrypted", refusal);
        Assert.Contains("join secret", refusal);
        Assert.Contains("token", refusal);
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://[::1]:8080")]
    public void Loopback_over_http_is_fine(string url)
    {
        // Nothing to sit on the path of, and this is the first-run path.
        var v = RemoteUrl.Check(url);

        Assert.True(v.Ok);
        Assert.Null(v.Warning);
    }

    [Fact]
    public void Insecure_allows_http_but_says_so_every_time()
    {
        var v = RemoteUrl.Check("http://weft.internal", allowInsecure: true);

        Assert.True(v.Ok);
        Assert.Equal("http://weft.internal", v.Url);
        Assert.NotNull(v.Warning);
    }

    [Fact]
    public void Insecure_does_not_turn_a_malformed_url_into_a_valid_one()
    {
        // The flag lowers exactly one bar. Letting it wave everything through is
        // how an escape hatch becomes the way in.
        Assert.False(RemoteUrl.Check("ftp://weft.example", allowInsecure: true).Ok);
        Assert.False(RemoteUrl.Check("", allowInsecure: true).Ok);
        Assert.False(RemoteUrl.Check("https://user:pw@weft.example", allowInsecure: true).Ok);
    }

    [Fact]
    public void Credentials_in_the_url_are_refused()
    {
        // They would be written to .weft/remote.json and repeated in every log
        // line and error message that quotes the URL.
        var v = RemoteUrl.Check("https://someone:hunter2@weft.example");

        Assert.False(v.Ok);
        Assert.Contains("credentials in the URL", v.Refusal);
    }

    [Fact]
    public void A_scheme_weft_does_not_speak_is_refused()
    {
        var v = RemoteUrl.Check("ssh://weft.example");

        Assert.False(v.Ok);
        Assert.Contains("ssh", v.Refusal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_is_not_a_url(string url) => Assert.False(RemoteUrl.Check(url).Ok);

    [Fact]
    public void A_saved_http_remote_is_warned_about_but_never_refused()
    {
        // Locking someone out of their own server over a setting they chose
        // earlier would strand their work to make a point.
        Assert.NotNull(RemoteUrl.WarnAbout("http://weft.example"));
        Assert.Null(RemoteUrl.WarnAbout("https://weft.example"));
        Assert.Null(RemoteUrl.WarnAbout("http://localhost:8080"));
    }
}

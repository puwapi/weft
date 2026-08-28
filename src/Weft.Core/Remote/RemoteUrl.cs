namespace Weft.Core.Remote;

/// <summary>The outcome of judging a server URL.</summary>
/// <param name="Url">The URL to use, normalised. Meaningful only when accepted.</param>
/// <param name="Refusal">Why it was refused, in terms a person can act on. Null when accepted.</param>
/// <param name="Warning">Accepted, but with something worth saying out loud.</param>
public sealed record UrlVerdict(string Url, string? Refusal, string? Warning)
{
    public bool Ok => Refusal is null;
}

/// <summary>
/// Decides whether a server URL is safe to send credentials to.
/// </summary>
/// <remarks>
/// <para>Content is encrypted before it leaves the machine, so plain HTTP would
/// not expose a single file. Two things travel in the clear regardless, and
/// neither is protected by the workspace key: the join secret, which buys
/// enrolment, and the bearer token, which every later request carries. Someone
/// on the path who takes either can enrol, download every object and fill the
/// disk. They still cannot read anything, which is exactly why this is a warning
/// about credentials and not about content, and why the message says so: a
/// refusal that overstates the danger gets bypassed by reflex.</para>
///
/// <para>Loopback is exempt because there is no path to sit on, and because
/// running a server locally is how the tool is tried for the first time.</para>
/// </remarks>
public static class RemoteUrl
{
    /// <summary>Judges a URL, optionally allowing plain HTTP to a non-local host.</summary>
    public static UrlVerdict Check(string raw, bool allowInsecure = false)
    {
        var text = (raw ?? "").Trim();

        if (text.Length == 0)
            return Refuse("A server URL is required, for example https://weft.example.");

        // A bare host is the common way to type this, and assuming HTTPS can only
        // ever be safer than what was typed. Assuming HTTP would be the reverse,
        // which is why the default is never inferred from a port or a name.
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return Refuse($"'{raw}' is not a URL. Expected something like https://weft.example.");

        if (uri.Scheme is not ("http" or "https"))
            return Refuse($"weft speaks HTTP, not '{uri.Scheme}'. Expected https://{uri.Host}.");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return Refuse(
                "Do not put credentials in the URL. They would be written to .weft/remote.json and "
                + "repeated in every log and error message along the way. Pass --join instead.");

        var normalised = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');

        if (uri.Scheme == "https") return new UrlVerdict(normalised, null, null);

        // Loopback: nothing to intercept, and this is the first-run path.
        if (uri.IsLoopback) return new UrlVerdict(normalised, null, null);

        if (!allowInsecure)
            return Refuse(
                $"Refusing to send credentials over plain HTTP to {uri.Host}.\n"
                + "Your files would still be encrypted, but the join secret and this machine's token "
                + "would not: anyone on the path could take them, enrol, and download every object.\n"
                + $"Use https://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}, or pass --insecure "
                + "if that host is only reachable over a private network you trust.");

        return new UrlVerdict(normalised, null,
            $"Plain HTTP to {uri.Host}: the join secret and this machine's token travel unprotected. "
            + "Your files stay encrypted. Only do this on a network nobody else is on.");
    }

    /// <summary>
    /// Judges a URL already saved in a workspace.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Check"/> because a stored remote is never refused:
    /// locking someone out of their own server over a setting they chose earlier
    /// would strand their work to make a point. It only says the thing out loud
    /// again, on every push and pull, so it does not become invisible.
    /// </remarks>
    public static string? WarnAbout(string savedUrl)
        => Uri.TryCreate(savedUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == "http"
            && !uri.IsLoopback
            ? $"This workspace talks to {uri.Host} over plain HTTP. Files stay encrypted; this "
              + "machine's token does not."
            : null;

    private static UrlVerdict Refuse(string why) => new("", why, null);
}

using System.Globalization;

namespace Weft.Core.Release;

/// <summary>What to do about a version found upstream.</summary>
public enum UpdateVerdict
{
    /// <summary>Already the published version.</summary>
    UpToDate,

    /// <summary>A newer one exists.</summary>
    Available,

    /// <summary>This build is newer than anything published. A local build, normally.</summary>
    Ahead,

    /// <summary>One of the two versions could not be read.</summary>
    Unknown,
}

public sealed record UpdateDecision(UpdateVerdict Verdict, Version? Current, Version? Latest)
{
    /// <summary>
    /// Compares this build with what is published.
    /// </summary>
    /// <remarks>
    /// Release tags carry a leading 'v' and build versions do not, so one side is
    /// always trimmed. Refusing to decide when either side is unreadable is
    /// deliberate: treating an unparseable version as "older" would push an
    /// update on somebody running a build they made themselves.
    /// </remarks>
    public static UpdateDecision Between(string currentVersion, string latestTag)
    {
        var current = Parse(currentVersion);
        var latest = Parse(latestTag);

        if (current is null || latest is null) return new UpdateDecision(UpdateVerdict.Unknown, current, latest);

        return new UpdateDecision(
            latest > current ? UpdateVerdict.Available
            : latest == current ? UpdateVerdict.UpToDate
            : UpdateVerdict.Ahead,
            current, latest);
    }

    private static Version? Parse(string text)
    {
        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];

        // Build metadata and pre-release suffixes are cut: '0.3.0+abc123' is the
        // same release as '0.3.0', and comparing them as strings would offer an
        // update to the version already running.
        var cut = t.IndexOfAny(['+', '-']);
        if (cut > 0) t = t[..cut];

        return Version.TryParse(t, out var v) ? v : null;
    }

    public string Describe() => Verdict switch
    {
        UpdateVerdict.UpToDate => $"already on {Current}",
        UpdateVerdict.Available => $"{Current} is installed, {Latest} is published",
        UpdateVerdict.Ahead => $"{Current} is newer than the published {Latest}",
        _ => "could not compare versions",
    };

    public override string ToString() => Describe();

    /// <summary>Formats a version the way a release tag is written.</summary>
    public static string Tag(Version v) => "v" + v.ToString(3);

    internal static string Invariant(int n) => n.ToString(CultureInfo.InvariantCulture);
}

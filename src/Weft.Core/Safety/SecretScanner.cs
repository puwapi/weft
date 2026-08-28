using System.Text;
using System.Text.RegularExpressions;

namespace Weft.Core.Safety;

/// <summary>Something that should not leave this machine.</summary>
/// <param name="Kind">What it looks like.</param>
/// <param name="Line">1-based line it was found on.</param>
/// <param name="Excerpt">Enough context to find it, with the secret itself masked.</param>
public sealed record SecretFinding(string Kind, int Line, string Excerpt);

/// <summary>
/// Looks for credentials in content about to be recorded.
/// </summary>
/// <remarks>
/// <para>The ignore rules govern paths, and a path-based rule cannot help here:
/// carried work is a patch over git-tracked files, so a key pasted into a source
/// file while debugging is in a path nobody would ever have listed. Everything
/// recorded reaches the server, so this is the last place to catch it.</para>
///
/// <para>Deliberately narrow. Patterns that fire on benign code train people to
/// pass --force by reflex, and a scanner everyone bypasses is worse than none:
/// it costs friction and buys nothing. Every pattern here matches a shape that is
/// almost never anything but a real credential.</para>
/// </remarks>
public static partial class SecretScanner
{
    private static readonly (string Kind, Regex Pattern)[] Patterns =
    [
        ("private key",        PrivateKey()),
        ("AWS access key",     AwsKey()),
        ("GitHub token",       GitHubToken()),
        ("Stripe live key",    StripeLive()),
        ("Slack token",        SlackToken()),
        ("Google API key",     GoogleKey()),
        ("OpenAI key",         OpenAiKey()),
        ("Anthropic key",      AnthropicKey()),
        ("JSON Web Token",     JsonWebToken()),
        ("connection string with password", ConnectionString()),
        ("PEM certificate bundle",          PemBlock()),
    ];

    /// <summary>Scans UTF-8 content. Binary is skipped: a false positive on a JPEG helps nobody.</summary>
    public static IReadOnlyList<SecretFinding> Scan(ReadOnlySpan<byte> content)
    {
        if (content.IndexOf((byte)0) >= 0) return [];

        var text = Encoding.UTF8.GetString(content);
        var findings = new List<SecretFinding>();
        var lines = text.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length is 0 or > 4000) continue;   // a minified bundle is not where secrets hide

            foreach (var (kind, pattern) in Patterns)
            {
                var m = pattern.Match(line);
                if (!m.Success) continue;

                findings.Add(new SecretFinding(kind, i + 1, Mask(line, m)));
                break;   // one finding per line is enough to stop the snapshot
            }
        }

        return findings;
    }

    /// <summary>
    /// Keeps enough of the line to locate it, and none of the secret.
    /// </summary>
    /// <remarks>
    /// The message goes to a terminal, into scrollback, and often into a paste.
    /// Reporting "you are about to leak this key" by printing the key would be
    /// its own leak.
    /// </remarks>
    private static string Mask(string line, Match match)
    {
        var trimmed = line.TrimStart();
        var offset = match.Index - (line.Length - trimmed.Length);

        var masked = offset >= 0 && offset < trimmed.Length
            ? string.Concat(
                trimmed.AsSpan(0, offset),
                match.Length > 8 ? match.Value[..4] + new string('*', 12) : new string('*', 8),
                trimmed.AsSpan(Math.Min(trimmed.Length, offset + match.Length)))
            : new string('*', 16);

        return masked.Length > 120 ? masked[..120] + "..." : masked;
    }

    // A prefix plus a fixed length is what makes these safe to match: the shape is
    // issued by one service and is not something a person types by accident.
    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")] private static partial Regex PrivateKey();
    [GeneratedRegex(@"-----BEGIN CERTIFICATE-----")] private static partial Regex PemBlock();
    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")] private static partial Regex AwsKey();
    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b")] private static partial Regex GitHubToken();
    [GeneratedRegex(@"\b(?:sk|rk)_live_[A-Za-z0-9]{20,}\b")] private static partial Regex StripeLive();
    [GeneratedRegex(@"\bxox[abposr]-[A-Za-z0-9-]{10,}\b")] private static partial Regex SlackToken();
    [GeneratedRegex(@"\bAIza[0-9A-Za-z_\-]{35}\b")] private static partial Regex GoogleKey();
    [GeneratedRegex(@"\bsk-[A-Za-z0-9]{20,}\b")] private static partial Regex OpenAiKey();
    [GeneratedRegex(@"\bsk-ant-[A-Za-z0-9\-_]{20,}\b")] private static partial Regex AnthropicKey();
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}")] private static partial Regex JsonWebToken();

    // A password inside a URL. The scheme list keeps it to things that really are
    // connection strings, and requiring a non-trivial password keeps it off
    // 'postgres://user@localhost' and off documentation placeholders.
    [GeneratedRegex(@"\b(?:postgres(?:ql)?|mysql|mongodb(?:\+srv)?|redis|amqp|mssql)://[^\s:/@]+:(?!password\b|changeme\b|xxx+\b|\*+\b|<)[^\s:/@]{6,}@")]
    private static partial Regex ConnectionString();
}

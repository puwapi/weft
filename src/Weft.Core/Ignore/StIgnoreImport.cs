namespace Weft.Core.Ignore;

/// <summary>Result of reading an existing Syncthing '.stignore'.</summary>
public sealed record StIgnoreImport
{
    /// <summary>Rules for '.weftignore'.</summary>
    public required IReadOnlyList<string> Ignore { get; init; }

    /// <summary>
    /// Rules that look confidential and were routed to '.weftnever'.
    /// Always reported to the user: this is a guess, and a guess that moves a
    /// rule into a set the user cannot override has to be visible.
    /// </summary>
    public required IReadOnlyList<string> Never { get; init; }

    /// <summary>Lines carried over as comments because weft has no equivalent.</summary>
    public required IReadOnlyList<string> Unsupported { get; init; }
}

/// <summary>
/// Imports an existing Syncthing ignore file.
/// </summary>
/// <remarks>
/// This exists because the first thing anyone adopting weft already has is a
/// hand-tuned '.stignore'. Re-deriving it from scratch loses intent that took
/// real incidents to learn, and the rules most worth keeping are exactly the
/// ones whose reason is no longer obvious.
/// </remarks>
public static class StIgnoreReader
{
    /// <summary>
    /// Substrings that make a rule a candidate for the confidentiality set.
    /// </summary>
    /// <remarks>
    /// Deliberately biased towards over-classifying. Putting a rule in 'never'
    /// that belonged in 'ignore' costs one file not syncing, and the user is told
    /// and can move it back. The opposite mistake sends a secret to the remote,
    /// and nobody is told.
    /// </remarks>
    private static readonly string[] ConfidentialHints =
    [
        "secret", "credential", "password", "passwd", "token", "apikey", "api_key",
        "key", "cert", "pem", "pfx", ".p12", "keystore", "private",
        ".env", ".cer", "audit", "rgpd", "gdpr", "id_rsa", "id_ed25519",
    ];

    /// <summary>
    /// Words in a section comment that mark everything under it as confidential.
    /// </summary>
    /// <remarks>
    /// The intent behind a rule usually lives in the comment above it, not in the
    /// pattern. A '.stignore' typically reads:
    ///
    ///     // Sensitive documents: never propagate these
    ///     internal-docs/
    ///     archive.zip
    ///
    /// Neither pattern contains a confidential-looking word, so keyword matching
    /// on the pattern alone loses exactly the rules whose reason was worth
    /// writing down. Both languages are covered because ignore files are written
    /// by people, in their own.
    /// </remarks>
    private static readonly string[] SectionHints =
    [
        "sensitive", "sensible", "secret", "confidential", "confidentiel",
        "private", "prive", "privé", "never", "jamais",
        "credential", "identifiant", "rgpd", "gdpr", "audit",
        "cryptograph", "certificat", "certificate",
    ];

    public static StIgnoreImport Parse(string text)
    {
        var ignore = new List<string>();
        var never = new List<string>();
        var unsupported = new List<string>();

        // Whether the comment block most recently seen declares its section
        // confidential. A blank line ends a section, which is how these files are
        // laid out in practice.
        var sectionIsConfidential = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                sectionIsConfidential = false;
                continue;
            }

            // Syncthing comments start with '//', weft's with '#'.
            if (line.StartsWith("//", StringComparison.Ordinal))
            {
                var comment = line[2..].Trim();
                if (comment.Length > 0 && MentionsConfidentiality(comment))
                    sectionIsConfidential = true;
                ignore.Add("# " + comment);
                continue;
            }

            if (line.StartsWith("#include", StringComparison.OrdinalIgnoreCase))
            {
                unsupported.Add(line);
                continue;
            }

            // Syncthing flag prefixes. '(?i)' is case-insensitive matching and
            // '(?d)' permits deletion; neither has a weft equivalent, so the flag
            // is dropped and the pattern kept. Dropping the pattern too would
            // silently lose a rule the user relies on.
            var flagged = false;
            while (line.StartsWith("(?", StringComparison.Ordinal))
            {
                var close = line.IndexOf(')');
                if (close < 0) break;
                line = line[(close + 1)..].Trim();
                flagged = true;
            }
            if (line.Length == 0) continue;

            var negated = line.StartsWith('!');
            var body = negated ? line[1..] : line;

            if (!negated && (sectionIsConfidential || LooksConfidential(body)))
            {
                never.Add(body);
                if (flagged) unsupported.Add($"{raw.Trim()}  (flags dropped)");
                continue;
            }

            ignore.Add(line);
            if (flagged) unsupported.Add($"{raw.Trim()}  (flags dropped)");
        }

        return new StIgnoreImport { Ignore = ignore, Never = never, Unsupported = unsupported };
    }

    private static bool LooksConfidential(string pattern)
    {
        var p = pattern.ToLowerInvariant();
        return ConfidentialHints.Any(h => p.Contains(h, StringComparison.Ordinal));
    }

    private static bool MentionsConfidentiality(string comment)
    {
        var c = comment.ToLowerInvariant();
        return SectionHints.Any(h => c.Contains(h, StringComparison.Ordinal));
    }
}

namespace Weft.Core.Ignore;

/// <summary>What weft will do with a path.</summary>
public enum IgnoreVerdict
{
    /// <summary>Snapshot it.</summary>
    Include = 0,

    /// <summary>Skip it: regenerable output. Overridable by the user.</summary>
    Ignored = 1,

    /// <summary>
    /// Refuse it: confidential. NOT overridable, not by --force.
    /// Private keys, certificates, environment files, audit material.
    /// </summary>
    Never = 2,
}

/// <summary>
/// Two rule sets that exist for unrelated reasons and must not be merged.
/// </summary>
/// <remarks>
/// <para><b>ignore</b> is cleanliness: build output, dependency trees, caches.
/// A user may legitimately want to override one.</para>
///
/// <para><b>never</b> is confidentiality. Because the remote receives whatever is
/// snapshotted, a mistake here does not stay local. It is therefore checked
/// first, it wins over every negation, and no flag defeats it.</para>
///
/// <para>Negation ('!') applies only within the ignore set. Following git, a file
/// cannot be re-included when one of its parent directories is excluded, which
/// is what makes directory pruning sound: if a directory is ignored, nothing
/// inside it can come back, so the walker never has to descend to find out.</para>
/// </remarks>
public sealed class IgnorePolicy
{
    private readonly List<(Glob Pattern, bool Negated)> _ignore;
    private readonly List<Glob> _never;
    private readonly HashSet<string> _exempt;

    private IgnorePolicy(List<(Glob, bool)> ignore, List<Glob> never, HashSet<string> exempt)
    {
        _ignore = ignore;
        _never = never;
        _exempt = exempt;
    }

    /// <summary>Builds a policy from the text of a .weftignore and a .weftnever file.</summary>
    public static IgnorePolicy Parse(string ignoreText, string neverText)
    {
        var ignore = new List<(Glob, bool)>();
        foreach (var raw in SplitLines(ignoreText))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var negated = line[0] == '!';
            if (negated) line = line[1..];

            var g = Glob.Parse(line);
            if (g is not null) ignore.Add((g, negated));
        }

        var never = new List<Glob>();
        var exempt = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in SplitLines(neverText))
        {
            var line = raw.Trim();

            // '=name' exempts one exact name from the never set.
            //
            // This is deliberately NOT negation. A negation glob can re-include
            // a whole class by accident; an exemption is a single literal name,
            // so its blast radius is exactly one filename and a reviewer can see
            // the full list at a glance. It exists because '.env.example' is
            // universal, contains no secrets by definition, and must survive a
            // rule that refuses '.env.*'.
            if (line.StartsWith('='))
            {
                var name = line[1..].Trim();
                if (name.Length == 0) continue;
                if (name.AsSpan().IndexOfAny('*', '?', '[') >= 0)
                    throw new InvalidOperationException(
                        $"An exemption must be a literal name, not a pattern: '{raw}'. " +
                        "Patterns here would reopen the confidentiality boundary.");
                exempt.Add(name);
                continue;
            }

            // A '!' in the never set would be a way to poke a hole in the
            // confidentiality boundary. It is rejected rather than honoured, and
            // rather than silently dropped: silently dropping it would let
            // someone believe an exception applied when it did not.
            if (line.StartsWith('!'))
                throw new InvalidOperationException(
                    $"Negation is not allowed in the 'never' set: '{raw}'. " +
                    "The never set is a confidentiality boundary, not a filter.");

            var g = Glob.Parse(line);
            if (g is not null) never.Add(g);
        }

        return new IgnorePolicy(ignore, never, exempt);
    }

    /// <summary>Verdict for one path, relative to the weft root, '/'-separated.</summary>
    public IgnoreVerdict Match(string relativePath, bool isDirectory)
    {
        if (!IsExempt(relativePath))
        {
            foreach (var g in _never)
                if (g.IsMatch(relativePath, isDirectory))
                    return IgnoreVerdict.Never;
        }

        // Later rules win, as in gitignore, so the scan runs backwards and stops
        // at the first hit instead of evaluating every rule.
        for (var i = _ignore.Count - 1; i >= 0; i--)
        {
            var (pattern, negated) = _ignore[i];
            if (pattern.IsMatch(relativePath, isDirectory))
                return negated ? IgnoreVerdict.Include : IgnoreVerdict.Ignored;
        }

        return IgnoreVerdict.Include;
    }

    /// <summary>
    /// Whether the walker should descend into a directory.
    /// </summary>
    /// <remarks>
    /// This is the single most performance-relevant call in weft. On the
    /// reference tree it turns 1.8 million visited entries into roughly 43 000:
    /// 'node_modules' is rejected once, at its own directory entry, and its
    /// contents are never stat'd.
    /// </remarks>
    public bool ShouldDescend(string relativeDirPath)
        => Match(relativeDirPath, isDirectory: true) == IgnoreVerdict.Include;

    /// <summary>True when the path's exact name, or the path itself, is exempted.</summary>
    private bool IsExempt(string relativePath)
    {
        if (_exempt.Count == 0) return false;
        if (_exempt.Contains(relativePath)) return true;

        var slash = relativePath.LastIndexOf('/');
        return slash >= 0 && _exempt.Contains(relativePath[(slash + 1)..]);
    }

    private static IEnumerable<string> SplitLines(string text)
        => text.Split('\n', StringSplitOptions.None).Select(l => l.TrimEnd('\r'));
}

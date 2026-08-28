namespace Weft.Core.Ignore;

/// <summary>
/// A gitignore-style glob, pre-parsed into segments so that matching a path
/// costs no allocation. Paths are '/'-separated and relative to the weft root.
/// </summary>
/// <remarks>
/// Matching walks the path with spans rather than splitting it. On the reference
/// tree the ignore set is consulted around 1.8 million times per full scan, so
/// allocating a string[] per path would dominate the scan.
/// </remarks>
public sealed class Glob
{
    private readonly string[] _segments;

    /// <summary>True when the pattern only ever matches directories (trailing '/').</summary>
    public bool DirectoryOnly { get; }

    private Glob(string[] segments, bool directoryOnly)
    {
        _segments = segments;
        DirectoryOnly = directoryOnly;
    }

    /// <summary>Parses one gitignore-style pattern. Returns null for blank lines and comments.</summary>
    public static Glob? Parse(string pattern)
    {
        var p = pattern.Trim();
        if (p.Length == 0 || p[0] == '#') return null;

        var dirOnly = p.EndsWith('/');
        if (dirOnly) p = p[..^1];
        if (p.Length == 0) return null;

        // gitignore: a pattern containing no '/' matches at any depth, so it is
        // equivalent to '**/<pattern>'. A pattern that does contain one is
        // anchored to the root. Normalising here keeps the matcher itself
        // uniform, with no special case for anchoring.
        var anchored = p.Contains('/');
        if (p.StartsWith('/')) p = p[1..];
        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        if (!anchored) parts = ["**", .. parts];
        return new Glob(parts, dirOnly);
    }

    /// <summary>Matches a '/'-separated relative path.</summary>
    public bool IsMatch(string path, bool isDirectory)
    {
        if (DirectoryOnly && !isDirectory) return false;
        return Match(0, path.AsSpan());
    }

    private bool Match(int pi, ReadOnlySpan<char> path)
    {
        while (true)
        {
            if (pi == _segments.Length) return path.IsEmpty;

            var seg = _segments[pi];

            if (seg == "**")
            {
                // '**' matches zero or more segments: try the remainder at this
                // position first, then at every following segment boundary.
                if (Match(pi + 1, path)) return true;
                while (!path.IsEmpty)
                {
                    var slash = path.IndexOf('/');
                    if (slash < 0) return false;
                    path = path[(slash + 1)..];
                    if (Match(pi + 1, path)) return true;
                }
                return false;
            }

            if (path.IsEmpty) return false;

            var cut = path.IndexOf('/');
            var head = cut < 0 ? path : path[..cut];
            if (!SegmentMatch(seg.AsSpan(), head)) return false;

            path = cut < 0 ? [] : path[(cut + 1)..];
            pi++;
        }
    }

    /// <summary>Matches one path segment. '*' and '?' never cross a '/', by construction.</summary>
    private static bool SegmentMatch(ReadOnlySpan<char> pat, ReadOnlySpan<char> s)
    {
        int p = 0, i = 0, starP = -1, starI = 0;

        while (i < s.Length)
        {
            if (p < pat.Length && pat[p] == '*')
            {
                starP = p++;
                starI = i;
                continue;
            }

            if (p < pat.Length && TryMatchOne(pat, ref p, s[i]))
            {
                i++;
                continue;
            }

            if (starP < 0) return false;

            // Backtrack: let the last '*' swallow one more character.
            p = starP + 1;
            i = ++starI;
        }

        while (p < pat.Length && pat[p] == '*') p++;
        return p == pat.Length;
    }

    /// <summary>Consumes one pattern atom (literal, '?', or a '[...]' class) against one character.</summary>
    private static bool TryMatchOne(ReadOnlySpan<char> pat, ref int p, char c)
    {
        if (pat[p] == '?') { p++; return true; }

        if (pat[p] == '[')
        {
            var close = pat[p..].IndexOf(']');
            if (close < 0) { var lit = pat[p] == c; p++; return lit; }   // unterminated: literal '['

            var body = pat.Slice(p + 1, close - 1);
            p += close + 1;

            var negate = body.Length > 0 && (body[0] == '!' || body[0] == '^');
            if (negate) body = body[1..];

            var hit = false;
            for (var k = 0; k < body.Length; k++)
            {
                if (k + 2 < body.Length && body[k + 1] == '-')
                {
                    if (c >= body[k] && c <= body[k + 2]) hit = true;
                    k += 2;
                }
                else if (body[k] == c) hit = true;
            }
            return hit != negate;
        }

        if (pat[p] == c) { p++; return true; }
        return false;
    }

    /// <summary>The original pattern, for diagnostics.</summary>
    public override string ToString() => string.Join('/', _segments) + (DirectoryOnly ? "/" : "");
}

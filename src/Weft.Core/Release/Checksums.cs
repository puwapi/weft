namespace Weft.Core.Release;

/// <summary>Reads the SHA256SUMS file published beside the binaries.</summary>
/// <remarks>
/// Parsed here rather than matched with a substring at the call site. A download
/// verified against the wrong line is worse than one not verified at all: it
/// reports success.
/// </remarks>
public static class Checksums
{
    /// <summary>The expected digest for one asset, or null when it is not listed.</summary>
    public static string? For(string sumsFile, string assetName)
    {
        foreach (var raw in sumsFile.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // '<64 hex>  <name>', as sha256sum writes it. The separator is two
            // spaces in text mode and ' *' in binary mode, so the split is on
            // whitespace rather than on a literal.
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var digest = parts[0];
            if (digest.Length != 64) continue;

            var name = parts[^1].TrimStart('*');

            // Compared whole, never as a prefix or a substring: 'weft-linux-x64'
            // is a prefix of nothing here today, but a future 'weft-linux-x64-musl'
            // would silently match it.
            if (string.Equals(name, assetName, StringComparison.Ordinal))
                return digest.ToLowerInvariant();
        }

        return null;
    }
}

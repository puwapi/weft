using System.Globalization;
using System.Text;

namespace Weft.Core.Store;

/// <summary>One file, as a snapshot records it.</summary>
/// <param name="Path">Relative to the workspace root, '/'-separated.</param>
/// <param name="Size">Length in bytes.</param>
/// <param name="ModifiedUtc">Last write time, to the millisecond.</param>
/// <param name="Executable">
/// The only permission bit weft carries. Read and write bits are a property of
/// the account that will hold the file, not of the content, and copying them
/// between machines with different users is how a synchroniser makes files
/// unreadable on arrival.
/// </param>
/// <param name="Chunks">
/// The content, in order. Concatenating these chunks reproduces the file exactly.
/// </param>
public sealed record FileEntry(
    string Path,
    long Size,
    DateTimeOffset ModifiedUtc,
    bool Executable,
    IReadOnlyList<ChunkId> Chunks);

/// <summary>
/// The list of files a snapshot holds.
/// </summary>
/// <remarks>
/// <para>A manifest is stored as an ordinary object: serialised, chunked by the
/// same content-defined chunker, and put in the same store.</para>
///
/// <para>That is the whole reason there is no delta format here. Because entries
/// are sorted and the manifest is chunked by content, adding a file changes the
/// one chunk that line lands in and leaves the rest identical. The incremental
/// property everyone wants from a delta chain falls out of the storage layer,
/// without the parts a delta chain drags along: no compaction schedule, no
/// "materialise a full manifest every N snapshots", and no chain to walk before
/// a manifest can be read.</para>
///
/// <para>Measured on the workspace this was built against: 9 113 files produce a
/// manifest of roughly 1.4 MB, so a snapshot that touches five files rewrites
/// about five chunks of it.</para>
/// </remarks>
public sealed class Manifest
{
    public const string Header = "weft-manifest 1";

    /// <summary>Entries, sorted by path with ordinal comparison.</summary>
    /// <remarks>
    /// Ordinal, never culture-aware. A culture-aware sort orders 'a' and 'A'
    /// differently depending on the machine's locale, so two machines would
    /// produce different bytes for identical content, defeating deduplication in
    /// a way that only shows up across a border.
    /// </remarks>
    public IReadOnlyList<FileEntry> Entries { get; }

    public Manifest(IEnumerable<FileEntry> entries)
        => Entries = entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();

    public long TotalBytes => Entries.Sum(e => e.Size);

    public byte[] Serialise()
    {
        var sb = new StringBuilder(Entries.Count * 128);
        sb.Append(Header).Append('\n');

        foreach (var e in Entries)
        {
            sb.Append(Escape(e.Path)).Append('\t')
              .Append(e.Size.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(e.ModifiedUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(e.Executable ? 'x' : '-').Append('\t');

            for (var i = 0; i < e.Chunks.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(e.Chunks[i].ToString());
            }

            sb.Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static Manifest Parse(ReadOnlySpan<byte> data)
    {
        var text = Encoding.UTF8.GetString(data);
        var lines = text.Split('\n');

        if (lines.Length == 0 || lines[0] != Header)
            throw new InvalidDataException(
                $"not a weft manifest, or a format this build does not understand (expected '{Header}')");

        var entries = new List<FileEntry>(lines.Length);

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;

            var f = line.Split('\t');
            if (f.Length != 5) throw new InvalidDataException($"malformed manifest line {i + 1}");

            var chunks = f[4].Length == 0
                ? []
                : f[4].Split(',').Select(h => ChunkId.Parse(h)).ToList();

            entries.Add(new FileEntry(
                Unescape(f[0]),
                long.Parse(f[1], CultureInfo.InvariantCulture),
                DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(f[2], CultureInfo.InvariantCulture)),
                f[3] == "x",
                chunks));
        }

        return new Manifest(entries);
    }

    /// <summary>
    /// Escapes the three characters that would otherwise break the line format.
    /// </summary>
    /// <remarks>
    /// A tab or newline in a filename is legal on every platform weft targets and
    /// is exactly the input that turns a text format into a corruption bug.
    /// </remarks>
    private static string Escape(string path)
    {
        if (path.AsSpan().IndexOfAny('\\', '\t', '\n') < 0) return path;

        var sb = new StringBuilder(path.Length + 8);
        foreach (var c in path)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string Unescape(string path)
    {
        if (!path.Contains('\\')) return path;

        var sb = new StringBuilder(path.Length);
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] != '\\' || i + 1 >= path.Length) { sb.Append(path[i]); continue; }

            switch (path[++i])
            {
                case '\\': sb.Append('\\'); break;
                case 't': sb.Append('\t'); break;
                case 'n': sb.Append('\n'); break;
                default: sb.Append('\\').Append(path[i]); break;
            }
        }
        return sb.ToString();
    }
}

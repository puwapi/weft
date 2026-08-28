using System.Globalization;
using System.Text;
using Weft.Core.Store;

namespace Weft.Core.Tests.Store;

public class ManifestTests
{
    private static ChunkId Cid(string s) => ChunkId.Of(Encoding.UTF8.GetBytes(s));

    private static FileEntry Entry(string path, params string[] chunks) =>
        new(path, 100, DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), false,
            chunks.Select(Cid).ToList());

    [Fact]
    public void A_manifest_survives_a_round_trip()
    {
        var m = new Manifest([Entry("a.txt", "a"), Entry("dir/b.md", "b", "c")]);
        var back = Manifest.Parse(m.Serialise());

        Assert.Equal(m.Entries.Count, back.Entries.Count);
        Assert.Equal(m.Entries[0].Path, back.Entries[0].Path);
        Assert.Equal(m.Entries[1].Chunks, back.Entries[1].Chunks);
    }

    [Fact]
    public void An_empty_manifest_round_trips()
        => Assert.Empty(Manifest.Parse(new Manifest([]).Serialise()).Entries);

    [Theory]
    [InlineData("with\ttab.txt")]
    [InlineData("with\nnewline.txt")]
    [InlineData("with\\backslash.txt")]
    [InlineData("with\\ttricky.txt")]
    [InlineData("accentué/été.md")]
    [InlineData("日本語/ファイル.txt")]
    [InlineData("emoji-🧵.md")]
    public void A_path_that_would_break_the_line_format_survives(string path)
    {
        // Tabs and newlines are legal in filenames on every platform weft targets
        // and are exactly the input that turns a text format into a corruption
        // bug: without escaping, one such file shifts every field on its line.
        var back = Manifest.Parse(new Manifest([Entry(path, "x")]).Serialise());
        Assert.Equal(path, Assert.Single(back.Entries).Path);
    }

    [Fact]
    public void Entries_are_sorted_ordinally_and_not_by_culture()
    {
        // A culture-aware sort orders these differently depending on the machine's
        // locale, so two machines would serialise identical content to different
        // bytes and deduplication would quietly stop working across a border.
        var m = new Manifest([Entry("b"), Entry("A"), Entry("a"), Entry("B")]);
        Assert.Equal(["A", "B", "a", "b"], m.Entries.Select(e => e.Path));
    }

    [Theory]
    [InlineData("tr-TR")]   // dotted/dotless i, different collation
    [InlineData("de-DE")]   // '.' groups thousands, ',' is the decimal mark
    [InlineData("fr-FR")]   // narrow no-break space groups thousands
    [InlineData("ar-SA")]   // may substitute digits entirely
    public void A_manifest_reads_the_same_under_any_culture(string culture)
    {
        // Sizes and timestamps are LARGE on purpose: a value of 100 formats
        // identically in every culture, so a test using one passes whatever the
        // code does and guards nothing.
        //
        // What this actually catches is the WRITE side. Plain long formatting and
        // NumberStyles.Integer parsing are both culture-insensitive for positive
        // integers (verified: ASCII digits parse everywhere and group separators
        // are rejected), so the parser was never at risk. The hazard is someone
        // later adding a format string: 'e.Size.ToString("N0")' turns 1234567890
        // into '1.234.567.890' under de-DE and every manifest written on that
        // machine becomes unreadable. Confirmed by breaking it.
        var big = new FileEntry("big.bin", 1_234_567_890,
            DateTimeOffset.FromUnixTimeMilliseconds(1_756_000_000_000), false, [Cid("a")]);

        var reference = new Manifest([big]).Serialise();

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            Assert.Equal(reference, new Manifest([big]).Serialise());

            var back = Assert.Single(Manifest.Parse(reference).Entries);
            Assert.Equal(1_234_567_890, back.Size);
            Assert.Equal(1_756_000_000_000, back.ModifiedUtc.ToUnixTimeMilliseconds());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void The_executable_bit_survives_but_nothing_else_about_permissions()
    {
        var m = new Manifest([new FileEntry("run.sh", 10, DateTimeOffset.UnixEpoch, true, [Cid("x")])]);
        Assert.True(Manifest.Parse(m.Serialise()).Entries[0].Executable);
    }

    [Fact]
    public void A_file_with_no_chunks_round_trips()
    {
        // An empty file has no content, therefore no chunks, and must not be
        // confused with a missing one.
        var m = new Manifest([new FileEntry("empty.txt", 0, DateTimeOffset.UnixEpoch, false, [])]);
        Assert.Empty(Manifest.Parse(m.Serialise()).Entries[0].Chunks);
    }

    [Fact]
    public void A_foreign_format_is_refused_rather_than_half_read()
        => Assert.Throws<InvalidDataException>(
            () => Manifest.Parse("weft-manifest 99\nwhatever\n"u8));
}

public class ManifestDiffTests
{
    private static ChunkId Cid(string s) => ChunkId.Of(Encoding.UTF8.GetBytes(s));

    private static FileEntry E(string path, string chunk, long ms = 0) =>
        new(path, 10, DateTimeOffset.FromUnixTimeMilliseconds(ms), false, [Cid(chunk)]);

    [Fact]
    public void Identical_manifests_produce_no_diff()
    {
        var a = new Manifest([E("x", "1"), E("y", "2")]);
        Assert.True(ManifestDiff.Between(a, new Manifest([E("x", "1"), E("y", "2")])).IsEmpty);
    }

    [Fact]
    public void Additions_changes_and_removals_are_told_apart()
    {
        var before = new Manifest([E("keep", "1"), E("edit", "2"), E("drop", "3")]);
        var after = new Manifest([E("keep", "1"), E("edit", "2b"), E("new", "4")]);

        var d = ManifestDiff.Between(before, after);

        Assert.Equal("new", Assert.Single(d.Added).Path);
        Assert.Equal("edit", Assert.Single(d.Changed).To.Path);
        Assert.Equal("drop", Assert.Single(d.Removed).Path);
    }

    [Fact]
    public void A_new_timestamp_alone_is_not_a_change()
    {
        // A rebuild rewrites files byte for byte and moves every timestamp.
        // Treating that as a change would make every build produce a snapshot
        // full of files that did not actually move, and push nothing but noise.
        var before = new Manifest([E("built.js", "same", ms: 1000)]);
        var after = new Manifest([E("built.js", "same", ms: 9_999_999)]);

        Assert.True(ManifestDiff.Between(before, after).IsEmpty);
    }

    [Fact]
    public void Flipping_the_executable_bit_is_a_change()
    {
        var before = new Manifest([new FileEntry("s.sh", 1, DateTimeOffset.UnixEpoch, false, [Cid("a")])]);
        var after = new Manifest([new FileEntry("s.sh", 1, DateTimeOffset.UnixEpoch, true, [Cid("a")])]);

        Assert.Single(ManifestDiff.Between(before, after).Changed);
    }

    [Fact]
    public void Only_genuinely_new_chunks_are_reported_as_needing_transfer()
    {
        // Content moved to a different path costs nothing to send: the chunks are
        // already in the store. This is what makes a rename free.
        var before = new Manifest([E("old/path.txt", "content")]);
        var after = new Manifest([E("new/path.txt", "content")]);

        Assert.Equal(2, ManifestDiff.Between(before, after).Count);
        Assert.Empty(ManifestDiff.NewChunks(before, after));
    }
}

using System.Security.Cryptography;
using System.Text;
using Weft.Core.Store;

namespace Weft.Core.Tests.Store;

public class FastCdcTests
{
    /// <summary>
    /// Text with the shape of source code: repeated vocabulary, varied line
    /// lengths, occasional long runs. Random bytes would be an easier problem
    /// than the real one, and pure repetition a harder one.
    /// </summary>
    private static byte[] SourceLike(int approxBytes, int seed)
    {
        var rng = new Random(seed);
        string[] words =
        [
            "public", "static", "readonly", "var", "return", "if", "foreach", "await",
            "Task", "string", "int", "null", "true", "false", "new", "this", "=>",
            "Repository", "Checkout", "Snapshot", "Manifest", "chunk", "hash", "offset",
        ];

        var sb = new StringBuilder(approxBytes + 512);
        while (sb.Length < approxBytes)
        {
            var lineWords = rng.Next(2, 14);
            sb.Append(new string(' ', rng.Next(0, 4) * 4));
            for (var i = 0; i < lineWords; i++)
            {
                sb.Append(words[rng.Next(words.Length)]);
                sb.Append(i == lineWords - 1 ? ";" : " ");
            }
            sb.Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static List<byte[]> Chunks(byte[] data) =>
        FastCdc.Split(data).Select(c => data[c.Offset..(c.Offset + c.Length)]).ToList();

    private static HashSet<string> Hashes(byte[] data) =>
        Chunks(data).Select(c => Convert.ToHexString(SHA256.HashData(c))).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Chunks_reassemble_into_the_original_exactly()
    {
        var data = SourceLike(2_000_000, seed: 1);
        var rebuilt = Chunks(data).SelectMany(c => c).ToArray();
        Assert.Equal(data, rebuilt);
    }

    [Fact]
    public void Chunking_is_deterministic()
    {
        var data = SourceLike(500_000, seed: 2);
        Assert.Equal(FastCdc.Split(data).ToList(), FastCdc.Split(data).ToList());
    }

    [Fact]
    public void Every_chunk_but_the_last_respects_the_size_bounds()
    {
        var data = SourceLike(3_000_000, seed: 3);
        var chunks = Chunks(data);

        foreach (var c in chunks.SkipLast(1))
        {
            Assert.True(c.Length >= FastCdc.MinSize, $"chunk of {c.Length} bytes is below the minimum");
            Assert.True(c.Length <= FastCdc.MaxSize, $"chunk of {c.Length} bytes is above the maximum");
        }
    }

    [Fact]
    public void The_average_chunk_size_lands_near_the_target()
    {
        // Guards the masks. A transcription error in either produces chunks that
        // are far too small (store bloated with metadata) or far too large
        // (deduplication stops working), and nothing else would notice.
        var data = SourceLike(8_000_000, seed: 4);
        var chunks = Chunks(data);
        var avg = chunks.SkipLast(1).Average(c => c.Length);

        Assert.InRange(avg, FastCdc.AvgSize * 0.5, FastCdc.AvgSize * 2.0);
    }

    [Fact]
    public void Inserting_bytes_at_the_front_leaves_most_chunks_untouched()
    {
        // THE property content-defined chunking exists for.
        //
        // Fixed-size blocks would share nothing at all here: every block after
        // the insertion point shifts, so the whole file re-uploads. Boundaries
        // chosen by content re-align within a few kilobytes of the edit.
        var original = SourceLike(4_000_000, seed: 5);
        var edited = Encoding.UTF8.GetBytes("// a line added at the very top\n")
            .Concat(original).ToArray();

        var before = Hashes(original);
        var after = Hashes(edited);
        var shared = before.Intersect(after, StringComparer.Ordinal).Count();
        var kept = (double)shared / before.Count;

        Assert.True(kept > 0.90,
            $"only {kept:P1} of chunks survived a one-line insertion; content-defined " +
            "chunking is not doing its job (expected over 90%)");
    }

    [Fact]
    public void Editing_the_middle_leaves_both_ends_untouched()
    {
        var original = SourceLike(4_000_000, seed: 6);
        var mid = original.Length / 2;
        var edited = original[..mid]
            .Concat(Encoding.UTF8.GetBytes("\n// inserted in the middle\n"))
            .Concat(original[mid..]).ToArray();

        var before = Hashes(original);
        var kept = (double)before.Intersect(Hashes(edited), StringComparer.Ordinal).Count() / before.Count;

        Assert.True(kept > 0.90, $"only {kept:P1} of chunks survived a mid-file insertion");
    }

    [Fact]
    public void Highly_repetitive_content_still_respects_the_maximum()
    {
        // Degenerate input: if no boundary is ever found, the forced cut at
        // MaxSize is the only thing keeping a chunk from growing to the size of
        // the whole file, which would defeat deduplication entirely.
        var data = new byte[1_000_000];
        Array.Fill(data, (byte)'A');

        foreach (var c in Chunks(data).SkipLast(1))
            Assert.True(c.Length <= FastCdc.MaxSize);
    }

    [Fact]
    public void Data_smaller_than_the_minimum_is_a_single_chunk()
    {
        var data = SourceLike(500, seed: 7);
        Assert.Single(FastCdc.Split(data));
    }

    [Fact]
    public void Empty_input_yields_no_chunks()
        => Assert.Empty(FastCdc.Split(Array.Empty<byte>()));
}

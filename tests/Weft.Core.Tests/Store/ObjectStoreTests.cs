using System.Text;
using Weft.Core.Store;

namespace Weft.Core.Tests.Store;

public sealed class ObjectStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "weft-test-" + Guid.NewGuid().ToString("n"));
    private readonly ObjectStore _store;

    public ObjectStoreTests() => _store = new ObjectStore(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Content_survives_a_round_trip()
    {
        var data = Text(string.Concat(Enumerable.Repeat("weft keeps a monorepo in step. ", 500)));
        Assert.Equal(data, _store.Get(_store.Put(data)));
    }

    [Fact]
    public void Identical_content_stored_twice_occupies_one_object()
    {
        var data = Text("the same bytes");

        var a = _store.Put(data);
        var b = _store.Put(data);

        Assert.Equal(a, b);
        Assert.Equal(1, _store.Measure().Objects);
    }

    [Fact]
    public void Different_content_gets_different_ids()
        => Assert.NotEqual(_store.Put(Text("a")), _store.Put(Text("b")));

    [Fact]
    public void Empty_content_round_trips()
    {
        var id = _store.Put([]);
        Assert.Empty(_store.Get(id));
    }

    [Fact]
    public void A_tampered_object_is_refused_rather_than_served()
    {
        // The safety property the whole design rests on. A sync tool that hands
        // back quietly wrong bytes is worse than one that stops: the wrong bytes
        // propagate to every machine and overwrite the right ones.
        var id = _store.Put(Text(string.Concat(Enumerable.Repeat("original content. ", 200))));

        var path = _store.PathOf(id);
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.ThrowsAny<Exception>(() => _store.Get(id));
    }

    [Fact]
    public void A_truncated_object_is_refused()
    {
        var id = _store.Put(Text(string.Concat(Enumerable.Repeat("some content to compress. ", 300))));

        var path = _store.PathOf(id);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

        Assert.ThrowsAny<Exception>(() => _store.Get(id));
    }

    [Fact]
    public void Compressible_content_is_stored_smaller_than_it_arrived()
    {
        var data = Text(new string('x', 100_000));
        var id = _store.Put(data);

        Assert.True(new FileInfo(_store.PathOf(id)).Length < data.Length / 10);
    }

    [Fact]
    public void Incompressible_content_does_not_grow_beyond_the_header()
    {
        // Already-compressed content (images, archives) inflates under deflate.
        // Storing it raw is what keeps a store of media from being larger than
        // the files it holds.
        var rng = new Random(42);
        var data = new byte[64 * 1024];
        rng.NextBytes(data);

        var id = _store.Put(data);
        var stored = new FileInfo(_store.PathOf(id)).Length;

        Assert.True(stored <= data.Length + 8,
            $"random data grew from {data.Length} to {stored} bytes: it was compressed when it should not have been");
        Assert.Equal(data, _store.Get(id));
    }

    [Fact]
    public void Objects_are_sharded_so_no_directory_holds_them_all()
    {
        for (var i = 0; i < 200; i++) _store.Put(Text($"object number {i}"));

        var shards = Directory.GetDirectories(Path.Combine(_dir, "objects"));
        Assert.True(shards.Length > 20, $"only {shards.Length} shards for 200 objects");
    }

    [Fact]
    public void Contains_reflects_what_was_stored()
    {
        var id = _store.Put(Text("stored"));
        Assert.True(_store.Contains(id));
        Assert.False(_store.Contains(ChunkId.Of(Text("never stored"))));
    }

    [Fact]
    public async Task Concurrent_writers_of_the_same_content_all_succeed()
    {
        // Two machines syncing at once hit this constantly. Both write a temp
        // file and both rename onto the same name; losing that race is the normal
        // outcome, not an error, because the name is the hash so the content is
        // identical by construction.
        var data = Text(string.Concat(Enumerable.Repeat("contended content. ", 400)));

        var ids = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => _store.Put(data))));

        Assert.Single(ids.Distinct());
        Assert.Equal(data, _store.Get(ids[0]));
        Assert.Equal(1, _store.Measure().Objects);
    }

    [Fact]
    public void No_temp_files_are_left_behind()
    {
        for (var i = 0; i < 50; i++) _store.Put(Text($"content {i}"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "tmp")));
    }
}

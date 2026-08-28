using System.Text;
using Weft.Core.Crypto;
using Weft.Core.Store;

namespace Weft.Core.Tests.Crypto;

public class ChunkCipherTests
{
    private static readonly WorkspaceKey Key = WorkspaceKey.FromBytes(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    private static readonly WorkspaceKey Other = WorkspaceKey.FromBytes(Enumerable.Range(100, 32).Select(i => (byte)i).ToArray());

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Content_survives_a_round_trip()
    {
        var cipher = new ChunkCipher(Key);
        var data = Text(string.Concat(Enumerable.Repeat("weft chunk content. ", 300)));
        var id = ChunkId.Of(data);

        var (remoteId, blob) = cipher.Seal(data, id);

        Assert.Equal(data, cipher.Open(blob, id));
        Assert.Equal(RemoteId.Of(Key, id), remoteId);
    }

    [Fact]
    public void Empty_content_round_trips()
    {
        var cipher = new ChunkCipher(Key);
        var (_, blob) = cipher.Seal([], ChunkId.Of([]));
        Assert.Empty(cipher.Open(blob, ChunkId.Of([])));
    }

    [Fact]
    public void The_same_content_always_encrypts_to_the_same_bytes()
    {
        // Deduplication depends on this. With a random nonce the same file would
        // encrypt differently on every machine and in every snapshot, and the
        // server would keep one copy per upload: the content-addressed design
        // would stop paying for itself the moment encryption was turned on.
        var cipher = new ChunkCipher(Key);
        var data = Text("identical content");
        var id = ChunkId.Of(data);

        var (idA, blobA) = cipher.Seal(data, id);
        var (idB, blobB) = cipher.Seal(data, id);

        Assert.Equal(idA, idB);
        Assert.Equal(blobA, blobB);
    }

    [Fact]
    public void Two_machines_holding_the_same_key_agree_on_every_byte()
    {
        // The same property across processes: a second machine must compute the
        // same name and the same blob, or it re-uploads everything the first one
        // already stored.
        var data = Text("shared content");
        var id = ChunkId.Of(data);

        var (idA, blobA) = new ChunkCipher(Key).Seal(data, id);
        var (idB, blobB) = new ChunkCipher(WorkspaceKey.FromBytes(Key.ToBytes())).Seal(data, id);

        Assert.Equal(idA, idB);
        Assert.Equal(blobA, blobB);
    }

    [Fact]
    public void Different_content_never_reuses_a_nonce()
    {
        // Reusing a (key, nonce) pair across different plaintexts breaks AES-GCM
        // outright: it leaks the XOR of the plaintexts and, worse, the
        // authentication key. Deriving the nonce from the content hash is what
        // rules it out, so it is checked over a large sample rather than assumed.
        var cipher = new ChunkCipher(Key);
        var nonces = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 2000; i++)
        {
            var d = Text($"distinct content number {i}");
            var (_, blob) = cipher.Seal(d, ChunkId.Of(d));
            Assert.True(nonces.Add(Convert.ToHexString(blob.AsSpan(1, 12).ToArray())),
                $"nonce reused at iteration {i}");
        }
    }

    [Theory]
    [InlineData(0)]      // version byte
    [InlineData(3)]      // nonce
    [InlineData(20)]     // tag
    [InlineData(40)]     // ciphertext
    public void A_single_flipped_bit_anywhere_is_detected(int offset)
    {
        var cipher = new ChunkCipher(Key);
        var data = Text(string.Concat(Enumerable.Repeat("content to protect. ", 50)));
        var id = ChunkId.Of(data);
        var (_, blob) = cipher.Seal(data, id);

        blob[offset] ^= 0x01;

        Assert.Throws<DecryptionFailedException>(() => cipher.Open(blob, id));
    }

    [Fact]
    public void A_blob_from_a_different_workspace_is_refused()
    {
        var data = Text("content from another workspace");
        var id = ChunkId.Of(data);
        var (_, blob) = new ChunkCipher(Other).Seal(data, id);

        var ex = Assert.Throws<DecryptionFailedException>(() => new ChunkCipher(Key).Open(blob, id));
        Assert.Contains("different workspace key", ex.Message);
    }

    [Fact]
    public void A_blob_asked_for_under_the_wrong_name_is_refused()
    {
        // The server holds ciphertext and therefore cannot check that an object
        // filed under a name really holds that content. A malicious or broken
        // server could hand back a valid object under the wrong name; the caller
        // catches it here, because the name is authenticated and the plaintext is
        // re-hashed.
        var cipher = new ChunkCipher(Key);

        var wanted = Text("the chunk I asked for");
        var served = Text("a different chunk entirely");
        var (_, blob) = cipher.Seal(served, ChunkId.Of(served));

        Assert.Throws<DecryptionFailedException>(() => cipher.Open(blob, ChunkId.Of(wanted)));
    }

    [Fact]
    public void The_name_the_server_sees_is_not_the_hash_of_the_content()
    {
        // If it were, anyone holding the server could ask "do you have
        // SHA-256(some file I already have)?" and learn the answer.
        var data = Text("content the server must not be able to recognise");
        var chunkId = ChunkId.Of(data);

        Assert.NotEqual(chunkId.ToString(), RemoteId.Of(Key, chunkId).ToString());
    }

    [Fact]
    public void Two_workspaces_give_the_same_content_different_names()
    {
        // Otherwise a server hosting several workspaces would leak which of them
        // hold the same file.
        var data = Text("content stored by two unrelated workspaces");
        var chunkId = ChunkId.Of(data);

        Assert.NotEqual(RemoteId.Of(Key, chunkId), RemoteId.Of(Other, chunkId));
    }

    [Fact]
    public void The_nonce_in_the_clear_reveals_no_part_of_the_content_hash()
    {
        // The nonce travels unencrypted in every blob. Truncating the chunk id to
        // build it would hand the server 12 bytes of the plaintext hash, which is
        // enough to confirm a guess.
        var cipher = new ChunkCipher(Key);
        var data = Text("content whose hash must stay private");
        var id = ChunkId.Of(data);

        var (_, blob) = cipher.Seal(data, id);

        Span<byte> raw = stackalloc byte[ChunkId.ByteLength];
        id.WriteTo(raw);

        Assert.NotEqual(Convert.ToHexString(raw[..12]),
                        Convert.ToHexString(blob.AsSpan(1, 12)));
    }

    [Fact]
    public void The_overhead_stays_small_enough_to_ignore()
    {
        // 29 bytes on an 8 KB average chunk. Worth pinning: an accidental change
        // to the layout that doubled it would cost 0.35% of every store.
        var cipher = new ChunkCipher(Key);
        var data = new byte[8192];
        var (_, blob) = cipher.Seal(data, ChunkId.Of(data));

        Assert.Equal(29, ChunkCipher.Overhead);
        Assert.Equal(data.Length + 29, blob.Length);
    }
}

public class WorkspaceKeyTests
{
    [Fact]
    public void A_key_survives_being_written_down_and_typed_back()
    {
        var key = WorkspaceKey.Generate();
        Assert.Equal(key.ToBytes(), WorkspaceKey.Parse(key.ToDisplayString()).ToBytes());
    }

    [Fact]
    public void The_written_form_avoids_confusable_characters()
    {
        // This string gets copied by hand between machines, once, and a
        // transcription error surfaces much later as "this object failed
        // authentication", which points nowhere near the real cause.
        var display = WorkspaceKey.Generate().ToDisplayString();
        Assert.DoesNotContain(display, c => c is 'I' or 'L' or 'O' or 'U');
    }

    [Theory]
    [InlineData("O", "0")]
    [InlineData("I", "1")]
    [InlineData("L", "1")]
    [InlineData("U", "V")]
    public void A_character_a_person_would_confuse_is_accepted_as_what_they_meant(string typed, string meant)
    {
        var key = WorkspaceKey.Generate();
        var display = key.ToDisplayString();

        // Substituting the confusable character must land on the same key rather
        // than sending someone back to retype a key that was in fact correct.
        var confused = display.Replace(meant, typed, StringComparison.Ordinal);
        Assert.Equal(key.ToBytes(), WorkspaceKey.Parse(confused).ToBytes());
    }

    [Fact]
    public void Formatting_is_tolerant_of_how_it_was_pasted()
    {
        var key = WorkspaceKey.Generate();
        var display = key.ToDisplayString();

        foreach (var variant in new[]
                 {
                     display.ToLowerInvariant(),
                     display.Replace("-", ""),
                     display.Replace("-", " "),
                     "  " + display + "  ",
                 })
            Assert.Equal(key.ToBytes(), WorkspaceKey.Parse(variant).ToBytes());
    }

    [Fact]
    public void Rubbish_is_refused_rather_than_silently_truncated()
        => Assert.ThrowsAny<Exception>(() => WorkspaceKey.Parse("weft-not-a-real-key"));

    [Fact]
    public void The_fingerprint_identifies_a_key_without_revealing_it()
    {
        var key = WorkspaceKey.Generate();
        var fp = key.Fingerprint();

        Assert.Equal(fp, WorkspaceKey.FromBytes(key.ToBytes()).Fingerprint());
        Assert.NotEqual(fp, WorkspaceKey.Generate().Fingerprint());
        Assert.DoesNotContain(Convert.ToHexStringLower(key.ToBytes()), fp, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sub_keys_differ_from_each_other_and_from_the_master()
    {
        // Using one key for both encryption and naming is how a construction that
        // is sound on its own becomes unsound in combination.
        var key = WorkspaceKey.Generate();
        var master = Convert.ToHexString(key.ToBytes());

        var enc = Convert.ToHexString(typeof(WorkspaceKey)
            .GetProperty("EncryptionKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(key) as byte[] ?? []);
        var idk = Convert.ToHexString(typeof(WorkspaceKey)
            .GetProperty("IdentifierKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(key) as byte[] ?? []);

        Assert.NotEqual(enc, idk);
        Assert.NotEqual(master, enc);
        Assert.NotEqual(master, idk);
    }
}

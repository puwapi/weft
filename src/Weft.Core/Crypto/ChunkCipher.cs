using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Weft.Core.Store;

namespace Weft.Core.Crypto;

/// <summary>Raised when a blob does not authenticate, or does not hold what was asked for.</summary>
public sealed class DecryptionFailedException(string message) : Exception(message);

/// <summary>
/// Encrypts chunks so the server holds nothing it can read, without losing
/// deduplication.
/// </summary>
/// <remarks>
/// <para>Ordinary encryption would end deduplication: a fresh random nonce makes
/// the same content encrypt differently every time, so the server would store one
/// copy per machine per snapshot and the whole content-addressed design would
/// stop paying.</para>
///
/// <para>The nonce is therefore derived from the content's own hash. Identical
/// content encrypts to identical bytes, so it deduplicates exactly as before;
/// different content yields a different nonce, so the (key, nonce) pair is never
/// reused across different plaintexts, which is the one thing that would break
/// AES-GCM outright.</para>
///
/// <para>The cost is honest and worth stating: an attacker holding the server and
/// a candidate file can confirm whether that exact file is stored, because it
/// would encrypt to a name they can compute. They learn presence, never content.
/// That is the standard trade of convergent encryption, and the alternative is
/// storing every machine's copy of every file separately.</para>
/// </remarks>
public sealed class ChunkCipher(WorkspaceKey key)
{
    private const byte FormatVersion = 1;
    private const int NonceLength = 12;   // AES-GCM standard
    private const int TagLength = 16;
    public const int Overhead = 1 + NonceLength + TagLength;

    /// <summary>Encrypts a chunk and returns the blob to store, with the name it goes by.</summary>
    public (RemoteId Id, byte[] Blob) Seal(ReadOnlySpan<byte> plaintext, ChunkId chunkId)
    {
        var remoteId = RemoteId.Of(key, chunkId);

        Span<byte> nonce = stackalloc byte[NonceLength];
        DeriveNonce(chunkId, nonce);

        var blob = new byte[Overhead + plaintext.Length];
        blob[0] = FormatVersion;
        nonce.CopyTo(blob.AsSpan(1, NonceLength));

        using var aes = new AesGcm(key.EncryptionKey, TagLength);
        aes.Encrypt(
            nonce,
            plaintext,
            blob.AsSpan(Overhead),
            blob.AsSpan(1 + NonceLength, TagLength),
            // The name is authenticated, so a blob cannot be filed under a
            // different id: moving it makes it fail to decrypt rather than
            // silently returning the wrong content.
            associatedData: AssociatedData(remoteId));

        return (remoteId, blob);
    }

    /// <summary>
    /// Decrypts a blob and confirms it holds the chunk that was asked for.
    /// </summary>
    /// <exception cref="DecryptionFailedException">
    /// The blob was tampered with, filed under the wrong name, or holds different
    /// content than requested.
    /// </exception>
    public byte[] Open(ReadOnlySpan<byte> blob, ChunkId expected)
    {
        var plaintext = OpenWithName(key, blob, RemoteId.Of(key, expected));

        // The tag already proves the bytes are ours and untampered. This proves
        // they are the chunk the CALLER asked for, which is the property a server
        // holding ciphertext cannot check on our behalf.
        var actual = ChunkId.Of(plaintext);
        if (actual != expected)
            throw new DecryptionFailedException($"object holds chunk '{actual}', not the requested '{expected}'");

        return plaintext;
    }

    /// <summary>
    /// Decrypts a blob knowing only the name it was filed under.
    /// </summary>
    /// <remarks>
    /// Needed for exactly one case: the snapshot at the root of a fetch, whose
    /// plaintext id cannot be known before it is read. The caller must then
    /// re-derive the name from the plaintext and compare, which
    /// <see cref="Open(ReadOnlySpan{byte}, ChunkId)"/> does automatically and this
    /// cannot. It is public so that path can exist, and deliberately awkward to
    /// reach for: using it where the id IS known skips a real check.
    /// </remarks>
    public static byte[] OpenWithName(WorkspaceKey key, ReadOnlySpan<byte> blob, RemoteId name)
    {
        if (blob.Length < Overhead)
            throw new DecryptionFailedException("blob is too short to be an object");

        if (blob[0] != FormatVersion)
            throw new DecryptionFailedException(
                $"object format v{blob[0]}, this build understands v{FormatVersion}. Upgrade weft.");

        var plaintext = new byte[blob.Length - Overhead];

        try
        {
            using var aes = new AesGcm(key.EncryptionKey, TagLength);
            aes.Decrypt(
                blob.Slice(1, NonceLength),
                blob[Overhead..],
                blob.Slice(1 + NonceLength, TagLength),
                plaintext,
                associatedData: AssociatedData(name));
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new DecryptionFailedException(
                "object failed authentication: it was altered in transit or at rest, " +
                "or it was encrypted with a different workspace key");
        }

        return plaintext;
    }

    /// <summary>
    /// Derives the nonce from the content hash.
    /// </summary>
    /// <remarks>
    /// Keyed rather than a plain truncation of the chunk id: the nonce travels in
    /// the clear inside every blob, and a bare hash prefix would hand the server a
    /// piece of the plaintext hash it is not supposed to have.
    /// </remarks>
    private void DeriveNonce(ChunkId chunkId, Span<byte> nonce)
    {
        Span<byte> input = stackalloc byte[8 + ChunkId.ByteLength];
        "wnonce\0\0"u8.CopyTo(input);
        chunkId.WriteTo(input[8..]);

        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(key.IdentifierKey, input, mac);
        mac[..NonceLength].CopyTo(nonce);
    }

    private static byte[] AssociatedData(RemoteId id)
    {
        var aad = new byte[4 + RemoteId.HexLength];
        BinaryPrimitives.WriteUInt32LittleEndian(aad, FormatVersion);
        Encoding.ASCII.GetBytes(id.ToString(), aad.AsSpan(4));
        return aad;
    }
}

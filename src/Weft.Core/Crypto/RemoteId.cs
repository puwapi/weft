using System.Security.Cryptography;
using Weft.Core.Store;

namespace Weft.Core.Crypto;

/// <summary>
/// The name an object goes by on the server. Never the hash of its content.
/// </summary>
/// <remarks>
/// <para>A distinct type from <see cref="ChunkId"/> on purpose, even though both
/// are 32 bytes. Sending a <see cref="ChunkId"/> to the server would hand it the
/// SHA-256 of the plaintext, which lets anyone holding the server confirm whether
/// a file they already have is stored there. Making the two interchangeable would
/// mean one careless assignment leaks that, silently and for good. The compiler
/// refuses the mistake instead.</para>
/// </remarks>
public readonly record struct RemoteId(UInt128 High, UInt128 Low)
{
    public const int HexLength = 64;

    /// <summary>
    /// Derives the server-visible name of a chunk: HMAC(identifier key, chunk id).
    /// </summary>
    /// <remarks>
    /// Deterministic, so every machine holding the key computes the same name and
    /// deduplication survives encryption. Keyed, so the server cannot go the other
    /// way and cannot test a guess.
    /// </remarks>
    public static RemoteId Of(WorkspaceKey key, ChunkId chunk)
    {
        Span<byte> id = stackalloc byte[ChunkId.ByteLength];
        chunk.WriteTo(id);

        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(key.IdentifierKey, id, mac);

        return new RemoteId(Read(mac[..16]), Read(mac[16..]));
    }

    public override string ToString()
    {
        Span<byte> b = stackalloc byte[32];
        Write(b[..16], High);
        Write(b[16..], Low);
        return Convert.ToHexStringLower(b);
    }

    public static bool TryParse(ReadOnlySpan<char> hex, out RemoteId id)
    {
        id = default;
        if (hex.Length != HexLength) return false;

        Span<byte> b = stackalloc byte[32];
        for (var i = 0; i < 32; i++)
        {
            var hi = Nibble(hex[i * 2]);
            var lo = Nibble(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0) return false;
            b[i] = (byte)((hi << 4) | lo);
        }

        id = new RemoteId(Read(b[..16]), Read(b[16..]));
        return true;

        static int Nibble(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
    }

    public static RemoteId Parse(ReadOnlySpan<char> hex)
        => TryParse(hex, out var id) ? id : throw new FormatException("not a valid object id");

    private static UInt128 Read(ReadOnlySpan<byte> b)
    {
        UInt128 v = 0;
        foreach (var x in b) v = (v << 8) | x;
        return v;
    }

    private static void Write(Span<byte> b, UInt128 v)
    {
        for (var i = b.Length - 1; i >= 0; i--) { b[i] = (byte)(v & 0xFF); v >>= 8; }
    }
}

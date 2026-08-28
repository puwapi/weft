using System.Security.Cryptography;
using System.Text;

namespace Weft.Core.Store;

/// <summary>
/// The 256-entry table that drives content-defined chunk boundaries.
/// </summary>
/// <remarks>
/// <para><b>These values are frozen forever.</b> Every chunk boundary in every
/// store ever written depends on them. Changing one value re-cuts every file, so
/// nothing already stored would ever be found again by content and the whole
/// store would silently re-upload itself. A change here is a format break, not a
/// tweak.</para>
///
/// <para>The table is derived from SHA-256 rather than pasted in as 256 magic
/// constants. Both are equally arbitrary, but a derivation can be re-checked by
/// anyone in three lines, whereas a transcription error in a magic table produces
/// a subtly worse boundary distribution that no test would catch.</para>
/// </remarks>
public static class Gear
{
    /// <summary>Domain separator. Part of the frozen definition: changing it changes every value.</summary>
    private const string Seed = "weft/gear/v1/";

    /// <summary>
    /// The table itself. Exposed as an array rather than behind an accessor
    /// because the chunker indexes it once per byte of every file it reads: a
    /// property returning a span would rebuild the span on each of those hits.
    /// Treat it as read-only.
    /// </summary>
    public static readonly ulong[] Table = Build();

    private static ulong[] Build()
    {
        var t = new ulong[256];
        Span<byte> digest = stackalloc byte[32];

        for (var i = 0; i < 256; i++)
        {
            var input = Encoding.ASCII.GetBytes(Seed + i.ToString());
            SHA256.HashData(input, digest);
            t[i] = BitConverter.ToUInt64(digest[..8]);
        }

        return t;
    }
}

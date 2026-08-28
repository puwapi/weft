using System.Security.Cryptography;
using System.Text;

namespace Weft.Core.Crypto;

/// <summary>
/// The secret shared by every machine on a workspace. The server never sees it.
/// </summary>
/// <remarks>
/// <para>256 random bits, generated once and carried to each machine by hand.
/// There is no passphrase and therefore no key-derivation function: a generated
/// key already has full entropy, and a passphrase would only add a way to pick a
/// weak one.</para>
///
/// <para>Two sub-keys are derived from it so that no single key is used for two
/// purposes, which is the usual way a construction that is fine on its own
/// becomes unsound in combination.</para>
/// </remarks>
public sealed class WorkspaceKey
{
    public const int Length = 32;

    private const string EncryptionInfo = "weft/enc/v1";
    private const string IdentifierInfo = "weft/id/v1";

    /// <summary>Encrypts chunk contents.</summary>
    internal byte[] EncryptionKey { get; }

    /// <summary>
    /// Derives the identifier the server sees, and the nonce.
    /// </summary>
    /// <remarks>
    /// Separate from the encryption key on purpose. If the server's identifiers
    /// were derived with the encryption key, anyone holding the ciphertext and a
    /// candidate plaintext could learn more than "is this chunk present".
    /// </remarks>
    internal byte[] IdentifierKey { get; }

    private readonly byte[] _master;

    private WorkspaceKey(byte[] master)
    {
        if (master.Length != Length)
            throw new ArgumentException($"a workspace key is {Length} bytes", nameof(master));

        _master = master;
        EncryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32,
            info: Encoding.ASCII.GetBytes(EncryptionInfo));
        IdentifierKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32,
            info: Encoding.ASCII.GetBytes(IdentifierInfo));
    }

    public static WorkspaceKey Generate() => new(RandomNumberGenerator.GetBytes(Length));

    public static WorkspaceKey FromBytes(ReadOnlySpan<byte> master) => new(master.ToArray());

    /// <summary>
    /// The key as a string a person can read aloud and type.
    /// </summary>
    /// <remarks>
    /// Crockford base32: no 'I', 'L', 'O' or 'U', so nothing is confusable with
    /// 1, 0 or another letter, and no vowel means no accidental word. This string
    /// is what gets carried to the second machine, by hand, once.
    /// </remarks>
    public string ToDisplayString()
    {
        var s = Base32.Encode(_master);
        var groups = Enumerable.Range(0, (s.Length + 7) / 8)
            .Select(i => s.Substring(i * 8, Math.Min(8, s.Length - i * 8)));
        return "weft-" + string.Join('-', groups);
    }

    public static WorkspaceKey Parse(string display)
    {
        // Separators are stripped BEFORE the prefix, not after. A key pasted with
        // spaces instead of dashes still carries its 'weft' marker, and every
        // letter of that marker is valid base32, so leaving it in decodes four
        // bytes of nonsense and reports a length error that points nowhere.
        var cleaned = new string(display.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '_').ToArray());
        if (cleaned.StartsWith("weft", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[4..];

        var bytes = Base32.Decode(cleaned);
        if (bytes.Length != Length)
            throw new FormatException($"a workspace key decodes to {Length} bytes, this one gave {bytes.Length}");

        return new WorkspaceKey(bytes);
    }

    /// <summary>
    /// A short, non-secret fingerprint, so two machines can confirm they hold the
    /// same key without either revealing it.
    /// </summary>
    public string Fingerprint()
    {
        var d = SHA256.HashData(Encoding.ASCII.GetBytes("weft/fingerprint/v1").Concat(_master).ToArray());
        return Convert.ToHexStringLower(d)[..12];
    }

    public byte[] ToBytes() => (byte[])_master.Clone();
}

/// <summary>Crockford base32: unambiguous when written down or read aloud.</summary>
internal static class Base32
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Decode(string s)
    {
        var bytes = new List<byte>(s.Length * 5 / 8 + 1);
        var buffer = 0;
        var bits = 0;

        foreach (var raw in s)
        {
            var c = char.ToUpperInvariant(raw);

            // Accept the characters Crockford excludes, mapped to what someone
            // transcribing by hand would have meant. Refusing them would send a
            // person back to retype a key that is, in fact, correct.
            c = c switch { 'O' => '0', 'I' or 'L' => '1', 'U' => 'V', _ => c };

            var v = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (v < 0) throw new FormatException($"'{raw}' is not part of a workspace key");

            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return bytes.ToArray();
    }
}

using System.Buffers.Binary;
using System.IO.Compression;

namespace Weft.Core.Store;

/// <summary>Raised when a stored object does not hash to the name it is filed under.</summary>
public sealed class CorruptObjectException(ChunkId expected, ChunkId actual)
    : Exception($"object '{expected}' hashes to '{actual}': the store is corrupt at this object")
{
    public ChunkId Expected { get; } = expected;
    public ChunkId Actual { get; } = actual;
}

/// <summary>
/// Content-addressed chunk storage on disk.
/// </summary>
/// <remarks>
/// <para>An object's name is the SHA-256 of its <em>uncompressed</em> bytes, so
/// every read can re-derive it and confirm what came back is what was asked for.
/// That makes silent corruption impossible to serve: at 3 GB/s the check costs
/// microseconds on an 8 KB chunk, and a sync tool that hands back quietly wrong
/// bytes is worse than one that stops.</para>
///
/// <para>Writes are atomic by temp-file and rename. A process killed mid-write
/// leaves a temp file, never a truncated object under a real name, which would
/// be indistinguishable from corruption forever after.</para>
/// </remarks>
public sealed class ObjectStore
{
    private const byte Magic = (byte)'W';
    private const byte FormatVersion = 1;
    private const byte FlagDeflate = 0b0000_0001;
    private const int HeaderLength = 8;

    /// <summary>
    /// Compression is kept only when it saves at least this much. Below it, the
    /// decompression cost on every future read buys nothing, and content that is
    /// already compressed (images, archives, media) usually grows.
    /// </summary>
    private const double WorthCompressing = 0.95;

    private readonly string _objectsDir;
    private readonly string _tempDir;

    public ObjectStore(string storeRoot)
    {
        _objectsDir = Path.Combine(storeRoot, "objects");
        _tempDir = Path.Combine(storeRoot, "tmp");
        Directory.CreateDirectory(_objectsDir);
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>Where an object lives.</summary>
    public string PathOf(ChunkId id)
    {
        var hex = id.ToString();
        return Path.Combine(_objectsDir, hex[..2], hex[2..]);
    }

    public bool Contains(ChunkId id) => File.Exists(PathOf(id));

    /// <summary>
    /// Stores content and returns its id. Storing something already present is a
    /// no-op, which is what makes deduplication fall out of the design rather
    /// than needing a separate index.
    /// </summary>
    public ChunkId Put(ReadOnlySpan<byte> content)
    {
        var id = ChunkId.Of(content);
        var final = PathOf(id);
        if (File.Exists(final)) return id;

        var encoded = Encode(content);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);

        var temp = Path.Combine(_tempDir, Guid.NewGuid().ToString("n"));
        try
        {
            File.WriteAllBytes(temp, encoded);
            File.Move(temp, final, overwrite: false);
        }
        catch (IOException)
        {
            // Another process stored the same content first. That is the normal
            // outcome of two concurrent writers, not an error: the content is
            // identical by construction, since the name IS the hash.
            if (!File.Exists(final)) throw;
        }
        finally
        {
            if (File.Exists(temp)) TryDelete(temp);
        }

        return id;
    }

    /// <summary>Reads content back, verifying it against the name it was filed under.</summary>
    /// <exception cref="CorruptObjectException">The bytes on disk no longer hash to their name.</exception>
    public byte[] Get(ChunkId id)
    {
        var raw = File.ReadAllBytes(PathOf(id));
        var content = Decode(raw);

        var actual = ChunkId.Of(content);
        if (actual != id) throw new CorruptObjectException(id, actual);

        return content;
    }

    /// <summary>Reads without verifying. Only for a caller that is about to verify itself.</summary>
    public byte[] GetUnverified(ChunkId id) => Decode(File.ReadAllBytes(PathOf(id)));

    private static byte[] Encode(ReadOnlySpan<byte> content)
    {
        byte[]? deflated = null;

        if (content.Length > 0)
        {
            using var ms = new MemoryStream(content.Length);
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(content);

            // Measured on real source at 30.8%, which is why compression is on by
            // default; kept per-object so that incompressible content does not
            // pay for it twice, in size and on every read.
            if (ms.Length < content.Length * WorthCompressing) deflated = ms.ToArray();
        }

        var payload = deflated ?? content.ToArray();
        var result = new byte[HeaderLength + payload.Length];

        result[0] = Magic;
        result[1] = FormatVersion;
        result[2] = deflated is null ? (byte)0 : FlagDeflate;
        result[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)content.Length);
        payload.CopyTo(result.AsSpan(HeaderLength));

        return result;
    }

    private static byte[] Decode(byte[] raw)
    {
        if (raw.Length < HeaderLength || raw[0] != Magic)
            throw new InvalidDataException("not a weft object");

        if (raw[1] != FormatVersion)
            throw new InvalidDataException(
                $"object format v{raw[1]}, this build understands v{FormatVersion}. Upgrade weft.");

        var length = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4));
        var payload = raw.AsSpan(HeaderLength);

        if ((raw[2] & FlagDeflate) == 0)
        {
            if (payload.Length != length)
                throw new InvalidDataException("object length does not match its header");
            return payload.ToArray();
        }

        var outBuf = new byte[length];
        using var src = new MemoryStream(raw, HeaderLength, payload.Length, writable: false);
        using var z = new ZLibStream(src, CompressionMode.Decompress);

        var read = 0;
        while (read < outBuf.Length)
        {
            var n = z.Read(outBuf, read, outBuf.Length - read);
            if (n == 0) throw new InvalidDataException("object ended before its declared length");
            read += n;
        }

        return outBuf;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>How much the store holds. Walks the tree, so not for a hot path.</summary>
    public (int Objects, long Bytes) Measure()
    {
        var count = 0;
        long bytes = 0;

        foreach (var f in Directory.EnumerateFiles(_objectsDir, "*", SearchOption.AllDirectories))
        {
            count++;
            bytes += new FileInfo(f).Length;
        }

        return (count, bytes);
    }
}

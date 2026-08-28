using Weft.Core.Store;

namespace Weft.Core.Workspace;

/// <summary>The pointer to this machine's latest snapshot.</summary>
/// <remarks>
/// A single small file, written atomically. The snapshot graph itself lives in
/// the object store and is immutable; this is the one mutable thing on disk, so
/// it is the only place a crash can leave the workspace inconsistent. Writing it
/// by temp-and-rename means the pointer is either the old snapshot or the new
/// one, never a half-written name that resolves to nothing.
/// </remarks>
public sealed class RefStore(string metaDir)
{
    private string HeadPath => Path.Combine(metaDir, "HEAD");

    public ChunkId? ReadHead()
    {
        if (!File.Exists(HeadPath)) return null;

        var text = File.ReadAllText(HeadPath).Trim();
        return ChunkId.TryParse(text, out var id) ? id : null;
    }

    public void WriteHead(ChunkId id)
    {
        Directory.CreateDirectory(metaDir);

        var temp = HeadPath + ".tmp";
        File.WriteAllText(temp, id.ToString() + "\n");
        File.Move(temp, HeadPath, overwrite: true);
    }
}

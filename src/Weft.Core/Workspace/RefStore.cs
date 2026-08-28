using System.Text.Json;
using System.Text.Json.Serialization;
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

    // ---------- what other machines had, last time we looked ----------

    private string RemoteHeadsPath => Path.Combine(metaDir, "REMOTE_HEADS");

    /// <summary>
    /// Records where each other machine stood, by LOCAL snapshot id.
    /// </summary>
    /// <remarks>
    /// Written by a pull, which is the only moment both names of a snapshot are
    /// known at once: the keyed one the server files it under, and the one it has
    /// here. Recording it means a merge needs no network at all, and the division
    /// stays honest: pull fetches, merge reconciles.
    /// </remarks>
    public void WriteRemoteHeads(IReadOnlyList<RemoteHead> heads)
    {
        Directory.CreateDirectory(metaDir);

        var temp = RemoteHeadsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(heads, RefJson.Default.IReadOnlyListRemoteHead));
        File.Move(temp, RemoteHeadsPath, overwrite: true);
    }

    public IReadOnlyList<RemoteHead> ReadRemoteHeads()
    {
        if (!File.Exists(RemoteHeadsPath)) return [];

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(RemoteHeadsPath),
                RefJson.Default.IReadOnlyListRemoteHead) ?? [];
        }
        catch (JsonException) { return []; }
    }
}

/// <summary>Where another machine stood at the last pull.</summary>
public sealed record RemoteHead(
    [property: JsonPropertyName("machineId")] string MachineId,
    [property: JsonPropertyName("machineName")] string MachineName,
    [property: JsonPropertyName("snapshot")] string Snapshot,
    [property: JsonPropertyName("seenUtc")] DateTimeOffset SeenUtc);

[JsonSerializable(typeof(IReadOnlyList<RemoteHead>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public sealed partial class RefJson : JsonSerializerContext;

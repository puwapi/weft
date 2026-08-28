using System.Globalization;
using System.Text;

namespace Weft.Core.Store;

/// <summary>Where one repository stood, on one machine, at snapshot time.</summary>
/// <param name="Path">Checkout path relative to the workspace root.</param>
/// <param name="Remote">Origin URL, empty when there is none. This is the field that makes a lost repository recoverable.</param>
/// <param name="Branch">Checked-out branch, empty when detached.</param>
/// <param name="Head">Commit the tree is at.</param>
/// <param name="IsPrimary">False for a linked worktree.</param>
/// <param name="DirtyFiles">Count of modified or untracked files. Zero means the tree is clean.</param>
public sealed record RepoState(
    string Path,
    string Remote,
    string Branch,
    string Head,
    bool IsPrimary,
    int DirtyFiles);

/// <summary>Uncommitted work in one checkout, as recorded by a snapshot.</summary>
/// <param name="RepoPath">Checkout path, relative to the workspace root.</param>
/// <param name="BaseCommit">The commit the patch applies to.</param>
/// <param name="Branch">Branch it was taken on. Empty when detached.</param>
/// <param name="PatchChunks">The patch itself, in the object store.</param>
/// <param name="PatchBytes">Its size, so it can be reported without fetching it.</param>
/// <param name="ChangedFiles">How many files it touches.</param>
/// <param name="StagedFiles">How many were staged, so landing can say what it did not restore.</param>
public sealed record CarriedWork(
    string RepoPath,
    string BaseCommit,
    string Branch,
    IReadOnlyList<ChunkId> PatchChunks,
    long PatchBytes,
    int ChangedFiles,
    int StagedFiles);

/// <summary>
/// One recorded state of the workspace.
/// </summary>
/// <remarks>
/// <para>Content-addressed like everything else: a snapshot's id is the hash of
/// its own serialised form, so it cannot be edited after the fact without
/// changing its name.</para>
///
/// <para>Repository state is carried here rather than in the manifest because it
/// is not content. A manifest describes bytes weft is responsible for; a repo
/// state describes where git stood, which weft records but does not own. Mixing
/// them would invite a future change to start merging branch names as if they
/// were file contents, which is precisely the mistake this design exists to
/// avoid.</para>
/// </remarks>
public sealed record Snapshot
{
    public const string Header = "weft-snapshot 1";

    /// <summary>
    /// Hash of the whole serialised manifest. Verifies the reassembly.
    /// </summary>
    /// <remarks>
    /// Each chunk is verified individually on read, but their ORDER is not:
    /// a reordered list would yield bytes made of individually valid chunks.
    /// This hash is what closes that hole.
    /// </remarks>
    public required ChunkId ManifestId { get; init; }

    /// <summary>
    /// The manifest's chunks, in order.
    /// </summary>
    /// <remarks>
    /// The manifest is chunked like any other file rather than stored whole, and
    /// that is the entire reason snapshots are incremental. Entries are sorted,
    /// so adding one file rewrites the one chunk its line falls in; storing the
    /// manifest as a single object would re-upload all of it on every snapshot,
    /// which for the reference workspace is 1.4 MB to record a five-file change.
    /// </remarks>
    public required IReadOnlyList<ChunkId> ManifestChunks { get; init; }

    /// <summary>Snapshots this one follows. Empty for the first; more than one after a merge.</summary>
    public required IReadOnlyList<ChunkId> Parents { get; init; }

    public required string MachineId { get; init; }
    public required string MachineName { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Where every git checkout stood. Never merged across machines.</summary>
    public required IReadOnlyList<RepoState> Repos { get; init; }

    /// <summary>
    /// Uncommitted work, carried so that a branch living on one disk is no longer
    /// invisible everywhere else.
    /// </summary>
    /// <remarks>
    /// Recorded, never applied. Landing it on another machine is an explicit
    /// command, because silently writing a patch into a working tree is exactly
    /// the gesture that destroys whatever a parallel session was doing there.
    /// </remarks>
    public IReadOnlyList<CarriedWork> Carried { get; init; } = [];

    public required int FileCount { get; init; }
    public required long TotalBytes { get; init; }

    public byte[] Serialise()
    {
        var sb = new StringBuilder(512 + Repos.Count * 160);
        sb.Append(Header).Append('\n');
        sb.Append("manifest ").Append(ManifestId).Append('\n');
        sb.Append("mchunks ").Append(string.Join(',', ManifestChunks)).Append('\n');

        foreach (var p in Parents) sb.Append("parent ").Append(p).Append('\n');

        sb.Append("machine ").Append(MachineId).Append('\n');
        sb.Append("name ").Append(Line(MachineName)).Append('\n');
        sb.Append("time ").Append(CreatedUtc.ToUnixTimeMilliseconds()).Append('\n');
        sb.Append("files ").Append(FileCount).Append('\n');
        sb.Append("bytes ").Append(TotalBytes).Append('\n');

        foreach (var c in Carried.OrderBy(c => c.RepoPath, StringComparer.Ordinal))
            sb.Append("carry ")
              .Append(Line(c.RepoPath)).Append('\t')
              .Append(c.BaseCommit).Append('\t')
              .Append(Line(c.Branch)).Append('\t')
              .Append(string.Join(',', c.PatchChunks)).Append('\t')
              .Append(c.PatchBytes.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(c.ChangedFiles.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(c.StagedFiles.ToString(CultureInfo.InvariantCulture)).Append('\n');

        foreach (var r in Repos.OrderBy(r => r.Path, StringComparer.Ordinal))
            sb.Append("repo ")
              .Append(Line(r.Path)).Append('\t')
              .Append(Line(r.Remote)).Append('\t')
              .Append(Line(r.Branch)).Append('\t')
              .Append(r.Head).Append('\t')
              .Append(r.IsPrimary ? 'p' : 'w').Append('\t')
              .Append(r.DirtyFiles).Append('\n');

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static Snapshot Parse(ReadOnlySpan<byte> data)
    {
        var lines = Encoding.UTF8.GetString(data).Split('\n');
        if (lines.Length == 0 || lines[0] != Header)
            throw new InvalidDataException($"not a weft snapshot (expected '{Header}')");

        ChunkId? manifest = null;
        var manifestChunks = new List<ChunkId>();
        var parents = new List<ChunkId>();
        var repos = new List<RepoState>();
        var carried = new List<CarriedWork>();
        string machineId = "", machineName = "";
        long time = 0, bytes = 0;
        var files = 0;

        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;

            var sp = line.IndexOf(' ');
            if (sp < 0) throw new InvalidDataException($"malformed snapshot line: '{line}'");

            var key = line[..sp];
            var value = line[(sp + 1)..];

            switch (key)
            {
                case "manifest": manifest = ChunkId.Parse(value); break;

                case "mchunks":
                    if (value.Length > 0)
                        manifestChunks.AddRange(value.Split(',').Select(h => ChunkId.Parse(h)));
                    break;
                case "parent": parents.Add(ChunkId.Parse(value)); break;
                case "machine": machineId = value; break;
                case "name": machineName = value; break;
                case "time": time = long.Parse(value, CultureInfo.InvariantCulture); break;
                case "files": files = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "bytes": bytes = long.Parse(value, CultureInfo.InvariantCulture); break;

                case "carry":
                    var cf = value.Split('\t');
                    if (cf.Length != 7) throw new InvalidDataException($"malformed carry line: '{line}'");
                    carried.Add(new CarriedWork(
                        cf[0], cf[1], cf[2],
                        cf[3].Length == 0 ? [] : cf[3].Split(',').Select(h => ChunkId.Parse(h)).ToList(),
                        long.Parse(cf[4], CultureInfo.InvariantCulture),
                        int.Parse(cf[5], CultureInfo.InvariantCulture),
                        int.Parse(cf[6], CultureInfo.InvariantCulture)));
                    break;

                case "repo":
                    var f = value.Split('\t');
                    if (f.Length != 6) throw new InvalidDataException($"malformed repo line: '{line}'");
                    repos.Add(new RepoState(f[0], f[1], f[2], f[3], f[4] == "p",
                        int.Parse(f[5], CultureInfo.InvariantCulture)));
                    break;

                // An unknown key is ignored rather than rejected, so a snapshot
                // written by a newer weft still parses here. Refusing it would
                // strand this machine the moment another one upgrades.
                default: break;
            }
        }

        if (manifest is null) throw new InvalidDataException("snapshot has no manifest");

        return new Snapshot
        {
            ManifestId = manifest.Value,
            ManifestChunks = manifestChunks,
            Parents = parents,
            MachineId = machineId,
            MachineName = machineName,
            CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(time),
            Repos = repos,
            Carried = carried,
            FileCount = files,
            TotalBytes = bytes,
        };
    }

    /// <summary>A snapshot's id is the hash of its own bytes.</summary>
    public ChunkId Id() => ChunkId.Of(Serialise());

    /// <summary>Strips the characters that would break the line format.</summary>
    private static string Line(string s) => s.Replace('\t', ' ').Replace('\n', ' ');
}

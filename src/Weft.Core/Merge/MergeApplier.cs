using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Core.Store;
using Weft.Core.Workspace;

namespace Weft.Core.Merge;

/// <summary>What applying a merge did.</summary>
public sealed record ApplyResult
{
    public required int Written { get; init; }
    public required int Deleted { get; init; }
    public required IReadOnlyList<string> Conflicts { get; init; }

    /// <summary>Files whose resolution was not obvious, with what happened.</summary>
    public required IReadOnlyList<(string Path, string Note)> Notes { get; init; }
}

/// <summary>A merge that is under way and waiting on a person.</summary>
public sealed record MergeState(
    [property: JsonPropertyName("ours")] string Ours,
    [property: JsonPropertyName("theirs")] string Theirs,
    [property: JsonPropertyName("conflicts")] IReadOnlyList<string> Conflicts,
    [property: JsonPropertyName("startedUtc")] DateTimeOffset StartedUtc);

[JsonSerializable(typeof(MergeState))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public sealed partial class MergeStateJson : JsonSerializerContext;

/// <summary>
/// Writes a decided merge to disk.
/// </summary>
/// <remarks>
/// <para>The only part of weft that touches the working tree, and it only ever
/// touches loose files. Anything inside a git checkout is absent from the
/// manifest by construction, because the walk stops at every repository: there is
/// no code path here that could reach one.</para>
///
/// <para><b>Conflict markers are never written into a file.</b> A file carrying
/// '&lt;&lt;&lt;&lt;&lt;&lt;&lt;' is broken for every tool that reads it, and if
/// nobody notices it stays broken. Instead the file on disk keeps working, holding
/// this machine's version, and the other version is written beside it. Nothing is
/// destroyed, nothing stops working, and the decision waits.</para>
/// </remarks>
public sealed class MergeApplier(WeftRoot root, ObjectStore store)
{
    /// <summary>Suffix for the other machine's version of a conflicted file.</summary>
    public const string TheirsSuffix = ".weft-theirs";

    /// <summary>Suffix for the common ancestor, so a manual three-way merge is possible.</summary>
    public const string BaseSuffix = ".weft-base";

    public const string StateFile = "MERGE";

    public ApplyResult Apply(MergeOutcome outcome)
    {
        var written = 0;
        var deleted = 0;
        var conflicts = new List<string>();
        var notes = new List<(string, string)>();

        // Paths that this filesystem cannot tell apart. A Linux machine can hold
        // 'README.md' and 'readme.md' at once; on macOS and Windows they are one
        // file, so writing both means the second silently replaces the first and
        // one machine's content disappears with nothing to say so.
        var colliding = CaseCollisions(outcome);

        foreach (var item in outcome.Items)
        {
            if (colliding.Contains(item.Path))
            {
                conflicts.Add(item.Path);
                continue;
            }

            switch (item.Action)
            {
                case MergeAction.Unchanged:
                case MergeAction.KeepOurs:
                    break;

                case MergeAction.TakeTheirs:
                case MergeAction.Write:
                    WriteFile(item.Path, item.Content!);
                    written++;
                    if (item.Note is not null) notes.Add((item.Path, item.Note));
                    break;

                case MergeAction.Delete:
                    if (DeleteFile(item.Path)) deleted++;
                    break;

                case MergeAction.Conflict:
                    MaterialiseConflict(outcome, item);
                    conflicts.Add(item.Path);
                    break;

                default:
                    throw new InvalidOperationException($"unhandled merge action {item.Action}");
            }
        }

        SaveState(conflicts.Count == 0
            ? null
            : new MergeState(outcome.Ours.ToString(), outcome.Theirs.ToString(), conflicts, DateTimeOffset.UtcNow));

        return new ApplyResult
        {
            Written = written,
            Deleted = deleted,
            Conflicts = conflicts,
            Notes = notes,
        };
    }

    /// <summary>
    /// Paths that differ only in case, on a filesystem that cannot tell them apart.
    /// </summary>
    /// <remarks>
    /// Reported as conflicts rather than resolved. Picking one is a decision about
    /// which machine's file survives, and weft has no basis for making it; writing
    /// both in turn makes the decision anyway, by arrival order, and says nothing.
    /// </remarks>
    private HashSet<string> CaseCollisions(MergeOutcome outcome)
        => FindCaseCollisions(outcome.Items, CaseInsensitive.Value);

    /// <summary>Exposed so both branches can be checked without a second filesystem.</summary>
    public static HashSet<string> FindCaseCollisions(IReadOnlyList<MergeItem> items, bool caseInsensitive)
    {
        if (!caseInsensitive) return [];

        var groups = items
            .Where(i => i.Action is MergeAction.TakeTheirs or MergeAction.Write or MergeAction.KeepOurs)
            .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(i => i.Path).Distinct(StringComparer.Ordinal).Count() > 1);

        return groups.SelectMany(g => g.Select(i => i.Path)).ToHashSet(StringComparer.Ordinal);
    }

    private Lazy<bool> CaseInsensitive => _caseInsensitive ??= new Lazy<bool>(ProbeCaseSensitivity);
    private Lazy<bool>? _caseInsensitive;

    /// <summary>
    /// Asks the filesystem rather than the operating system.
    /// </summary>
    /// <remarks>
    /// macOS is case-insensitive by default and case-sensitive when the volume was
    /// formatted that way; Linux is usually sensitive but not on a mounted exFAT
    /// share. Deciding from the OS name would be wrong on both, so a file is
    /// written and looked for under another case.
    /// </remarks>
    private bool ProbeCaseSensitivity()
    {
        var name = "weft-case-probe-" + Guid.NewGuid().ToString("n");
        var lower = Path.Combine(root.MetaPath, name);

        try
        {
            Directory.CreateDirectory(root.MetaPath);
            File.WriteAllBytes(lower, []);
            return File.Exists(Path.Combine(root.MetaPath, name.ToUpperInvariant()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Cannot tell. Assumed insensitive, which only ever costs a conflict
            // to resolve by hand; assuming the other way costs a lost file.
            return true;
        }
        finally
        {
            try { if (File.Exists(lower)) File.Delete(lower); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Puts the other version, and the ancestor, beside the file.
    /// </summary>
    /// <remarks>
    /// The file itself is left exactly as it is. Whatever was working before the
    /// merge keeps working while the decision is pending, which is the difference
    /// between a sync tool you can run mid-afternoon and one you cannot.
    /// </remarks>
    private void MaterialiseConflict(MergeOutcome outcome, MergeItem item)
    {
        var theirs = FindContent(outcome.Theirs, item.Path);
        if (theirs is not null) WriteFile(item.Path + TheirsSuffix, theirs);

        if (outcome.Base is null) return;

        var ancestor = FindContent(outcome.Base.Value, item.Path);
        if (ancestor is not null) WriteFile(item.Path + BaseSuffix, ancestor);
    }

    private byte[]? FindContent(ChunkId snapshotId, string path)
    {
        try
        {
            var snapshot = new SnapshotGraph(store).Load(snapshotId);

            var manifestParts = snapshot.ManifestChunks.Select(store.Get).ToList();
            var manifestBytes = new byte[manifestParts.Sum(p => p.Length)];
            var at = 0;
            foreach (var p in manifestParts) { p.CopyTo(manifestBytes, at); at += p.Length; }

            var entry = Manifest.Parse(manifestBytes).Entries.FirstOrDefault(e => e.Path == path);
            if (entry is null) return null;

            var parts = entry.Chunks.Select(store.Get).ToList();
            var buffer = new byte[parts.Sum(p => p.Length)];
            at = 0;
            foreach (var p in parts) { p.CopyTo(buffer, at); at += p.Length; }
            return buffer;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private void WriteFile(string relativePath, byte[] content)
    {
        var full = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // Written through a temp file and renamed. A process killed mid-write
        // would otherwise leave a truncated file that looks like the merge's
        // considered answer.
        var temp = full + ".weft-tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, full, overwrite: true);
    }

    private bool DeleteFile(string relativePath)
    {
        var full = Resolve(relativePath);
        if (!File.Exists(full)) return false;

        File.Delete(full);

        // A directory left empty by a deletion is noise, and it makes the tree
        // look like it still holds something. Removed upwards, stopping at the
        // first directory that is not empty and never at the root.
        var dir = Path.GetDirectoryName(full);
        while (dir is not null && !PathsEqual(dir, root.Path))
        {
            if (Directory.EnumerateFileSystemEntries(dir).Any()) break;
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }

        return true;
    }

    /// <summary>
    /// Turns a manifest path into an absolute one, refusing anything that escapes
    /// the workspace.
    /// </summary>
    /// <remarks>
    /// The manifest arrives from another machine. A path of '../../.ssh/authorized_keys'
    /// would be written wherever it pointed, so this is the boundary that makes a
    /// hostile or corrupt manifest unable to reach outside the workspace.
    /// </remarks>
    private string Resolve(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidOperationException($"refusing an absolute path from a manifest: '{relativePath}'");

        var full = Path.GetFullPath(Path.Combine(root.Path, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!full.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"refusing a path that escapes the workspace: '{relativePath}'");

        return full;
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.Ordinal);

    // ---------- state ----------

    private string StatePath => Path.Combine(root.MetaPath, StateFile);

    public MergeState? LoadState()
    {
        if (!File.Exists(StatePath)) return null;

        try { return JsonSerializer.Deserialize(File.ReadAllText(StatePath), MergeStateJson.Default.MergeState); }
        catch (JsonException) { return null; }
    }

    private void SaveState(MergeState? state)
    {
        Directory.CreateDirectory(root.MetaPath);

        if (state is null)
        {
            if (File.Exists(StatePath)) File.Delete(StatePath);
            return;
        }

        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, MergeStateJson.Default.MergeState));
    }

    public void ClearState() => SaveState(null);

    /// <summary>Conflicts whose companion files are still lying around, so still unresolved.</summary>
    public IReadOnlyList<string> UnresolvedOf(MergeState state)
        => state.Conflicts.Where(p => File.Exists(Resolve(p + TheirsSuffix))).ToList();

    /// <summary>Removes the companion files once a conflict has been settled.</summary>
    public void ClearCompanions(string relativePath)
    {
        foreach (var suffix in new[] { TheirsSuffix, BaseSuffix })
        {
            var p = Resolve(relativePath + suffix);
            if (File.Exists(p)) File.Delete(p);
        }
    }
}

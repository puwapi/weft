using Weft.Core.Git;
using Weft.Core.Ignore;
using Weft.Core.Workspace;

using Weft.Core.Tests.Support;

namespace Weft.Core.Tests.Git;

public sealed class TreeWalkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weft-walk-" + Guid.NewGuid().ToString("n"));

    public TreeWalkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        TempTree.Remove(_root);
    }

    private void File_(string relative, string content = "x")
    {
        var p = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
    }

    private void Dir_(string relative)
        => Directory.CreateDirectory(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private TreeWalkResult Run(string ignore = "", string never = "")
        => new TreeWalk(_root, IgnorePolicy.Parse(ignore, never)).Run();

    [Fact]
    public void Loose_files_are_found()
    {
        File_("README.md");
        File_("docs/guide.md");

        Assert.Equal(["README.md", "docs/guide.md"], Run().LooseFiles);
    }

    [Fact]
    public void A_repository_stops_the_walk_and_its_contents_are_not_loose()
    {
        // Everything under a repository belongs to git, which weft asks directly.
        // Walking in would both duplicate git's answer and be far slower.
        Dir_("myrepo/.git");
        File_("myrepo/src/main.cs");
        File_("myrepo/README.md");
        File_("outside.txt");

        var r = Run();

        Assert.Equal(["myrepo"], r.CheckoutRoots);
        Assert.Equal(["outside.txt"], r.LooseFiles);
    }

    [Fact]
    public void A_linked_worktree_is_recognised_even_though_its_dot_git_is_a_file()
    {
        // The failure this catches is quiet and large: a worktree whose '.git' is
        // a file gets walked as ordinary directories, so a whole checkout is
        // snapshotted as loose content. On the tree weft was built against that
        // was 3 696 files.
        File_(".wt-feature/.git", "gitdir: /somewhere/hub/.git/worktrees/feature");
        File_(".wt-feature/src/app.ts");

        var r = Run();

        Assert.Equal([".wt-feature"], r.CheckoutRoots);
        Assert.Empty(r.LooseFiles);
    }

    [Fact]
    public void Wefts_own_metadata_is_never_walked()
    {
        // Regression. Without this the second snapshot walks the object store the
        // first one wrote and stores it inside itself, so every snapshot roughly
        // doubles the store. It looks like real content because it is real
        // content, already recorded. Found by running two snapshots in a row.
        File_($"{WeftRoot.MetaDir}/store/objects/ab/cdef");
        File_($"{WeftRoot.MetaDir}/HEAD");
        File_("real.txt");

        Assert.Equal(["real.txt"], Run().LooseFiles);
    }

    [Fact]
    public void A_directory_named_like_the_metadata_dir_deeper_down_is_still_walked()
    {
        // Only the one at the root is weft's. A nested '.weft' belongs to whoever
        // put it there, and silently dropping it would lose their files.
        File_($"nested/{WeftRoot.MetaDir}/notes.md");

        Assert.Contains($"nested/{WeftRoot.MetaDir}/notes.md", Run().LooseFiles);
    }

    [Fact]
    public void An_ignored_directory_is_pruned_without_being_entered()
    {
        File_("node_modules/pkg/index.js");
        File_("node_modules/pkg/deep/nested/more.js");
        File_("src/app.ts");

        var r = Run(ignore: "node_modules");

        Assert.Equal(["src/app.ts"], r.LooseFiles);
        Assert.Equal(1, r.PrunedDirectories);

        // The point of pruning: entries inside were never even looked at.
        Assert.True(r.VisitedEntries < 5, $"{r.VisitedEntries} entries visited; the walk descended into a pruned directory");
    }

    [Fact]
    public void A_confidential_path_is_reported_rather_than_silently_dropped()
    {
        File_("secrets/id_ed25519");
        File_("app.ts");

        var r = Run(never: "id_ed25519");

        Assert.Equal(["app.ts"], r.LooseFiles);
        Assert.Equal(["secrets/id_ed25519"], r.Refused);
    }

    [Fact]
    public void Results_are_sorted_so_two_machines_agree()
    {
        // Directory enumeration order is filesystem-dependent. Unsorted output
        // would give two machines different manifests for identical content.
        foreach (var n in new[] { "z.md", "a.md", "m.md", "B.md" }) File_(n);

        Assert.Equal(["B.md", "a.md", "m.md", "z.md"], Run().LooseFiles);
    }

    [Fact]
    public void An_empty_workspace_walks_cleanly()
    {
        var r = Run();
        Assert.Empty(r.LooseFiles);
        Assert.Empty(r.CheckoutRoots);
    }
}

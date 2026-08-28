using System.Text;
using Weft.Core.Ignore;
using Weft.Core.Merge;
using Weft.Core.Store;
using Weft.Core.Workspace;

namespace Weft.Core.Tests.Merge;

public sealed class MergeApplierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "weft-apply-" + Guid.NewGuid().ToString("n"));
    private readonly WeftRoot _root;
    private readonly MergeApplier _applier;

    public MergeApplierTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, WeftRoot.MetaDir));
        _root = new WeftRoot
        {
            Path = _dir,
            Policy = IgnorePolicy.Parse(DefaultRules.Ignore, DefaultRules.Never),
            IsInitialised = true,
        };
        _applier = new MergeApplier(_root, new ObjectStore(Path.Combine(_dir, WeftRoot.MetaDir, "store")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private string Read(string rel) => File.ReadAllText(Path.Combine(_dir, rel));
    private bool Exists(string rel) => File.Exists(Path.Combine(_dir, rel));

    private void Given(string rel, string content)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private MergeOutcome Outcome(params MergeItem[] items) => new()
    {
        Base = null,
        Ours = ChunkId.Of(B("ours")),
        Theirs = ChunkId.Of(B("theirs")),
        FastForward = false,
        Items = items,
        Repos = [],
    };

    [Fact]
    public void Taking_their_version_writes_the_file()
    {
        Given("a.txt", "old\n");

        var r = _applier.Apply(Outcome(
            new MergeItem { Path = "a.txt", Action = MergeAction.TakeTheirs, Content = B("new\n") }));

        Assert.Equal("new\n", Read("a.txt"));
        Assert.Equal(1, r.Written);
    }

    [Fact]
    public void Keeping_our_version_touches_nothing()
    {
        Given("a.txt", "ours\n");

        var r = _applier.Apply(Outcome(new MergeItem { Path = "a.txt", Action = MergeAction.KeepOurs }));

        Assert.Equal("ours\n", Read("a.txt"));
        Assert.Equal(0, r.Written);
    }

    [Fact]
    public void A_deletion_removes_the_file_and_the_directory_it_emptied()
    {
        Given("deep/nested/gone.txt", "x\n");

        _applier.Apply(Outcome(new MergeItem { Path = "deep/nested/gone.txt", Action = MergeAction.Delete }));

        Assert.False(Exists("deep/nested/gone.txt"));
        Assert.False(Directory.Exists(Path.Combine(_dir, "deep")));
    }

    [Fact]
    public void A_deletion_never_removes_a_directory_that_still_holds_something()
    {
        Given("shared/gone.txt", "x\n");
        Given("shared/stays.txt", "y\n");

        _applier.Apply(Outcome(new MergeItem { Path = "shared/gone.txt", Action = MergeAction.Delete }));

        Assert.True(Exists("shared/stays.txt"));
    }

    [Fact]
    public void A_conflicted_file_is_left_exactly_as_it_was()
    {
        // The rule the design turns on. Whatever was working before the merge
        // keeps working while the decision waits.
        Given("code.cs", "our working version\n");

        _applier.Apply(Outcome(new MergeItem
        {
            Path = "code.cs",
            Action = MergeAction.Conflict,
            ConflictReason = "both changed it",
        }));

        Assert.Equal("our working version\n", Read("code.cs"));
        Assert.DoesNotContain("<<<<", Read("code.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_conflict_carrying_content_STILL_does_not_write_it()
    {
        // Defence in depth. The engine never puts content on a conflict, so the
        // test above passes even if the applier ignores the rule. This one hands
        // the applier a conflict that does carry content and checks it is
        // discarded: the guarantee has to hold here too, or a future change on
        // the engine side quietly turns into a destroyed file.
        Given("code.cs", "our working version\n");

        _applier.Apply(Outcome(new MergeItem
        {
            Path = "code.cs",
            Action = MergeAction.Conflict,
            ConflictReason = "both changed it",
            Content = B("SOMETHING THE ENGINE SHOULD NEVER HAVE ATTACHED\n"),
        }));

        Assert.Equal("our working version\n", Read("code.cs"));
    }

    [Fact]
    public void A_conflict_records_state_so_the_merge_can_be_finished_later()
    {
        Given("code.cs", "ours\n");

        var r = _applier.Apply(Outcome(new MergeItem
        {
            Path = "code.cs", Action = MergeAction.Conflict, ConflictReason = "both changed it",
        }));

        Assert.Equal(["code.cs"], r.Conflicts);

        var state = _applier.LoadState();
        Assert.NotNull(state);
        Assert.Equal(["code.cs"], state.Conflicts);
    }

    [Fact]
    public void A_clean_merge_leaves_no_state_behind()
    {
        Given("a.txt", "x\n");

        _applier.Apply(Outcome(
            new MergeItem { Path = "a.txt", Action = MergeAction.TakeTheirs, Content = B("y\n") }));

        Assert.Null(_applier.LoadState());
    }

    [Fact]
    public void A_note_is_carried_out_so_a_non_obvious_resolution_is_never_silent()
    {
        var r = _applier.Apply(Outcome(new MergeItem
        {
            Path = "notes.md",
            Action = MergeAction.Write,
            Content = B("merged\n"),
            Note = "both machines appended; kept both",
        }));

        var (path, note) = Assert.Single(r.Notes);
        Assert.Equal("notes.md", path);
        Assert.Contains("appended", note, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("../../.ssh/authorized_keys")]
    [InlineData("sub/../../escaped.txt")]
    public void A_path_that_escapes_the_workspace_is_refused(string path)
    {
        // The manifest arrives from another machine. Without this boundary, a
        // hostile or corrupt one could write anywhere the process can reach.
        Assert.Throws<InvalidOperationException>(() => _applier.Apply(Outcome(
            new MergeItem { Path = path, Action = MergeAction.TakeTheirs, Content = B("owned\n") })));
    }

    [Fact]
    public void An_absolute_path_is_refused()
    {
        var absolute = OperatingSystem.IsWindows() ? @"C:\Windows\evil.txt" : "/etc/evil.txt";

        Assert.Throws<InvalidOperationException>(() => _applier.Apply(Outcome(
            new MergeItem { Path = absolute, Action = MergeAction.TakeTheirs, Content = B("owned\n") })));
    }

    [Fact]
    public void A_path_merely_starting_with_the_workspace_name_is_still_refused()
    {
        // '/tmp/weft-apply-x' and '/tmp/weft-apply-x-evil' share a prefix as
        // strings but are different directories. Comparing without the separator
        // would let the second through.
        Assert.Throws<InvalidOperationException>(() => _applier.Apply(Outcome(
            new MergeItem { Path = "../" + Path.GetFileName(_dir) + "-evil/x.txt",
                            Action = MergeAction.TakeTheirs, Content = B("owned\n") })));
    }

    [Fact]
    public void No_temp_files_survive_a_write()
    {
        _applier.Apply(Outcome(
            new MergeItem { Path = "a.txt", Action = MergeAction.TakeTheirs, Content = B("x\n") }));

        Assert.DoesNotContain(Directory.GetFiles(_dir), f => f.EndsWith(".weft-tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Companion_files_are_never_snapshotted()
    {
        // Otherwise the next snapshot records the other machine's version as a new
        // file of our own, and pushing it sends the conflict to everybody.
        Given("code.cs", "ours\n");
        Given("code.cs" + MergeApplier.TheirsSuffix, "theirs\n");
        Given("code.cs" + MergeApplier.BaseSuffix, "base\n");

        var walk = new Weft.Core.Git.TreeWalk(_dir, _root.Policy).Run();

        Assert.Contains("code.cs", walk.LooseFiles);
        Assert.DoesNotContain(walk.LooseFiles, f => f.Contains("weft-theirs", StringComparison.Ordinal));
        Assert.DoesNotContain(walk.LooseFiles, f => f.Contains("weft-base", StringComparison.Ordinal));
    }
}

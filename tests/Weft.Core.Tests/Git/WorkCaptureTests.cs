using System.Text;
using Weft.Core.Git;

namespace Weft.Core.Tests.Git;

/// <summary>Exercises real git repositories, because the whole point is that git decides.</summary>
public sealed class WorkCaptureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "weft-capture-" + Guid.NewGuid().ToString("n"));
    private readonly GitRunner _git = new();
    private readonly WorkCapture _capture;

    public WorkCaptureTests()
    {
        Directory.CreateDirectory(_dir);
        _capture = new WorkCapture(_git);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> RepoAsync(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);

        // The branch is named here rather than left to git. init.defaultBranch is
        // 'master' on a stock install and 'main' on many configured ones, so a
        // test that assumes either fails for half the people who run it, on a
        // point that has nothing to do with what it is checking.
        await Run(path, "init", "-q", "-b", "main");
        await Run(path, "config", "user.email", "t@example.com");
        await Run(path, "config", "user.name", "t");

        Write(path, ".gitignore", "node_modules\n");
        Write(path, "tracked.txt", "line 1\nline 2\n");

        await Run(path, "add", "-A");
        await Run(path, "commit", "-q", "-m", "base");
        return path;
    }

    private async Task<GitResult> Run(string dir, params string[] args)
        => await _git.RunAsync(dir, default, args);

    private static void Write(string dir, string rel, string content)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private static Checkout At(string path) => new()
    {
        RelativePath = Path.GetFileName(path),
        AbsolutePath = path,
        CommonDir = Path.Combine(path, ".git"),
        IsPrimary = true,
    };

    private static string Text(WorkPatch p) => Encoding.UTF8.GetString(p.Patch);

    [Fact]
    public async Task A_clean_checkout_carries_nothing()
        => Assert.Null(await _capture.CaptureAsync(At(await RepoAsync("clean"))));

    [Fact]
    public async Task A_modified_tracked_file_is_captured()
    {
        var repo = await RepoAsync("modified");
        Write(repo, "tracked.txt", "line 1 CHANGED\nline 2\n");

        var patch = await _capture.CaptureAsync(At(repo));

        Assert.NotNull(patch);
        Assert.Contains("line 1 CHANGED", Text(patch), StringComparison.Ordinal);
        Assert.Equal(1, patch.ChangedFiles);
    }

    [Fact]
    public async Task An_UNTRACKED_file_is_captured_too()
    {
        // The half a plain 'git diff HEAD' misses entirely, and the half that
        // matters most: a new file that exists nowhere else.
        var repo = await RepoAsync("untracked");
        Write(repo, "brand-new.ts", "export const x = 1;\n");
        Write(repo, "deep/nested/also-new.ts", "export const y = 2;\n");

        var patch = await _capture.CaptureAsync(At(repo));

        Assert.NotNull(patch);
        Assert.Contains("brand-new.ts", Text(patch), StringComparison.Ordinal);
        Assert.Contains("deep/nested/also-new.ts", Text(patch), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ignored_files_never_travel()
    {
        var repo = await RepoAsync("ignored");
        Write(repo, "node_modules/huge.js", "a million lines\n");
        Write(repo, "real.ts", "export const x = 1;\n");

        var patch = await _capture.CaptureAsync(At(repo));

        Assert.NotNull(patch);
        Assert.Contains("real.ts", Text(patch), StringComparison.Ordinal);
        Assert.DoesNotContain("node_modules", Text(patch), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capturing_does_not_disturb_what_the_user_has_staged()
    {
        // The reason a throwaway index exists. Restaging someone's work while
        // taking a snapshot is far worse than missing a file: it changes what
        // their next commit would contain, silently.
        var repo = await RepoAsync("staging");
        Write(repo, "tracked.txt", "staged change\n");
        Write(repo, "unstaged.txt", "not staged\n");
        await Run(repo, "add", "tracked.txt");

        var before = (await Run(repo, "diff", "--cached", "--name-only")).StdOut;
        var patch = await _capture.CaptureAsync(At(repo));
        var after = (await Run(repo, "diff", "--cached", "--name-only")).StdOut;

        Assert.Equal(before, after);
        Assert.Equal("tracked.txt\n", after);

        // And both halves were still captured.
        Assert.NotNull(patch);
        Assert.Contains("staged change", Text(patch), StringComparison.Ordinal);
        Assert.Contains("unstaged.txt", Text(patch), StringComparison.Ordinal);
        Assert.Equal(1, patch.StagedFiles);
    }

    [Fact]
    public async Task A_deleted_tracked_file_is_captured_as_a_deletion()
    {
        var repo = await RepoAsync("deleted");
        File.Delete(Path.Combine(repo, "tracked.txt"));

        var patch = await _capture.CaptureAsync(At(repo));

        Assert.NotNull(patch);
        Assert.Contains("deleted file", Text(patch), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_patch_records_the_commit_it_applies_to()
    {
        var repo = await RepoAsync("anchored");
        Write(repo, "tracked.txt", "changed\n");

        var head = (await Run(repo, "rev-parse", "HEAD")).StdOut.Trim();
        var patch = await _capture.CaptureAsync(At(repo));

        Assert.NotNull(patch);
        Assert.Equal(head, patch.BaseCommit);
        Assert.Equal("main", patch.Branch);
    }

    [Fact]
    public async Task A_captured_patch_applies_to_a_fresh_clone_of_the_same_commit()
    {
        // The property the whole feature rests on: what is captured here has to
        // land there. Anything less makes the safety net decorative.
        var source = await RepoAsync("source");
        Write(source, "tracked.txt", "line 1 CHANGED\nline 2\n");
        Write(source, "brand-new.ts", "export const x = 1;\n");

        var patch = await _capture.CaptureAsync(At(source));
        Assert.NotNull(patch);

        var target = Path.Combine(_dir, "target");
        await _git.RunAsync(_dir, default, "clone", "-q", source, target);
        await Run(target, "checkout", "-q", patch.BaseCommit);

        var check = await _capture.CheckAsync(target, patch.Patch, threeWay: false);
        Assert.True(check.Ok, $"the patch did not apply: {check.StdErr}");

        var apply = await _capture.ApplyAsync(target, patch.Patch, threeWay: false);
        Assert.True(apply.Ok, apply.StdErr);

        Assert.Equal("line 1 CHANGED\nline 2\n", File.ReadAllText(Path.Combine(target, "tracked.txt")));
        Assert.Equal("export const x = 1;\n", File.ReadAllText(Path.Combine(target, "brand-new.ts")));
    }

    [Fact]
    public async Task Checking_a_patch_writes_nothing()
    {
        // 'land' checks before applying, so a refusal never leaves a half-applied
        // tree. That is only true if the check itself is inert.
        var source = await RepoAsync("checksource");
        Write(source, "tracked.txt", "changed\n");
        var patch = await _capture.CaptureAsync(At(source));

        var target = Path.Combine(_dir, "checktarget");
        await _git.RunAsync(_dir, default, "clone", "-q", source, target);
        await Run(target, "checkout", "-q", patch!.BaseCommit);

        var before = File.ReadAllText(Path.Combine(target, "tracked.txt"));
        await _capture.CheckAsync(target, patch.Patch, threeWay: false);

        Assert.Equal(before, File.ReadAllText(Path.Combine(target, "tracked.txt")));
    }

    [Fact]
    public async Task A_patch_that_does_not_apply_is_reported_before_anything_is_written()
    {
        var source = await RepoAsync("conflictsource");
        Write(source, "tracked.txt", "from the source\n");
        var patch = await _capture.CaptureAsync(At(source));

        // A target whose content the patch cannot line up with.
        var target = await RepoAsync("conflicttarget");
        Write(target, "tracked.txt", "something else entirely\nand another line\n");
        await Run(target, "commit", "-qam", "diverged");

        var check = await _capture.CheckAsync(target, patch!.Patch, threeWay: false);
        Assert.False(check.Ok);
        Assert.Equal("something else entirely\nand another line\n",
            File.ReadAllText(Path.Combine(target, "tracked.txt")));
    }
}

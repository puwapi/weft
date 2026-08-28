using System.Runtime.InteropServices;
using System.Text;
using Weft.Core.Git;
using Weft.Core.Merge;
using Weft.Core.Workspace;

namespace Weft.Core.Tests.Merge;

/// <summary>
/// Paths a filesystem cannot tell apart.
/// </summary>
/// <remarks>
/// A Linux machine can hold 'README.md' and 'readme.md' at once. On macOS and
/// Windows they are one file, so applying both means the second silently replaces
/// the first and one machine's content disappears with nothing to say so. This is
/// the class of bug that only appears when two people are on different systems,
/// which is precisely when nobody is watching.
/// </remarks>
public class CaseCollisionTests
{
    private static MergeItem Item(string path, MergeAction action = MergeAction.TakeTheirs)
        => new() { Path = path, Action = action, Content = Encoding.UTF8.GetBytes("x") };

    [Fact]
    public void On_a_case_insensitive_filesystem_both_names_are_refused()
    {
        // Both, not one. Picking a survivor is a decision weft has no basis for,
        // and writing them in turn makes the decision anyway, by arrival order.
        var found = MergeApplier.FindCaseCollisions(
            [Item("README.md"), Item("readme.md"), Item("other.txt")], caseInsensitive: true);

        Assert.Equal(2, found.Count);
        Assert.Contains("README.md", found);
        Assert.Contains("readme.md", found);
        Assert.DoesNotContain("other.txt", found);
    }

    [Fact]
    public void On_a_case_sensitive_filesystem_they_are_two_ordinary_files()
    {
        Assert.Empty(MergeApplier.FindCaseCollisions(
            [Item("README.md"), Item("readme.md")], caseInsensitive: false));
    }

    [Fact]
    public void A_collision_in_a_directory_name_counts_too()
    {
        var found = MergeApplier.FindCaseCollisions(
            [Item("Docs/guide.md"), Item("docs/guide.md")], caseInsensitive: true);

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void The_same_path_appearing_once_is_never_a_collision()
        => Assert.Empty(MergeApplier.FindCaseCollisions([Item("a.txt"), Item("b.txt")], caseInsensitive: true));

    [Fact]
    public void A_deletion_does_not_collide_with_anything()
    {
        // Removing 'README.md' while writing 'readme.md' is unambiguous on any
        // filesystem: one path goes, one arrives.
        Assert.Empty(MergeApplier.FindCaseCollisions(
            [Item("README.md", MergeAction.Delete), Item("readme.md")], caseInsensitive: true));
    }
}

public class PlatformDescriptionTests
{
    [Fact]
    public void A_machine_says_which_system_it_is_on_and_not_just_Unix()
    {
        // Environment.OSVersion.Platform reports "Unix" for macOS and for Linux
        // alike, so a machines table showing it cannot tell them apart, which is
        // the only thing the column is for.
        var platform = MachineIdentity.Mint("probe").Platform;

        Assert.Contains(platform.Split('-')[0], new[] { "macos", "linux", "windows", "unknown" });
        Assert.Contains(RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), platform);
        Assert.DoesNotContain("Unix", platform, StringComparison.Ordinal);
    }
}

public class VolatilePathTests
{
    [Fact]
    public void This_processs_own_temp_directory_counts_as_volatile()
    {
        // Asked for rather than assumed: on Windows it comes from TEMP and differs
        // per user, and on macOS it is a per-session path under /var/folders that
        // no hard-coded constant could name.
        var inTemp = Path.Combine(Path.GetTempPath(), "some-worktree");
        Assert.True(Checkout.IsUnderTemp(inTemp));
    }

    [Fact]
    public void An_ordinary_project_directory_does_not()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(Checkout.IsUnderTemp(Path.Combine(home, "projects", "weft")));
    }

    [Theory]
    [InlineData("/tmp/wt-hub")]
    [InlineData("/var/folders/s0/abc/T/wt-hub")]
    [InlineData("/private/var/folders/s0/abc/T/wt-hub")]
    public void Well_known_unix_temp_locations_count_even_when_they_are_not_ours(string path)
    {
        // A checkout can sit under a temp directory belonging to another user or
        // to the system, and it is just as likely to be reclaimed.
        if (OperatingSystem.IsWindows()) return;
        Assert.True(Checkout.IsUnderTemp(path));
    }
}
